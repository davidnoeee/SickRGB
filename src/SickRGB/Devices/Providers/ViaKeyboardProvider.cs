using System.Diagnostics;
using SickRGB.Core;
using SickRGB.Hardware;

namespace SickRGB.Devices.Providers;

/// <summary>
/// Drives keyboards that speak the VIA protocol over raw HID, which is what the
/// QMK-based Sharkoon SKILLER boards (SGK50 S3, S4 and relatives) use.
///
/// Detection is by capability, not by USB ID. Sharkoon's vendor ID (0x1EA7) is a shared
/// OEM identifier used by a great many unrelated keyboards, so claiming devices by ID
/// would grab hardware this code knows nothing about. Instead every raw-HID interface is
/// asked for its VIA protocol version, and only something that answers correctly is
/// adopted. The probe is a pure read and changes nothing.
///
/// What VIA can and cannot do is worth being clear about: it exposes a single colour for
/// the whole board (hue, saturation and brightness), not per-key streaming. Per-key
/// control needs firmware-specific commands that are not part of VIA. So the keyboard
/// appears as one light rather than a row of zones - accurate, rather than pretending to
/// a resolution the protocol does not have.
///
/// Nothing here writes to the keyboard's flash: `id_custom_save` is deliberately never
/// sent, so everything is volatile and the board returns to its own settings when
/// unplugged.
/// </summary>
public sealed class ViaKeyboardProvider : ILightProvider
{
    // QMK's raw HID endpoint.
    private const ushort UsagePageRawHid = 0xFF60;
    private const ushort UsageRawHid = 0x61;

    /// <summary>VIA payload size. Windows prepends a report id byte, hence 33 on the wire.</summary>
    private const int PayloadLength = 32;
    private const int BufferLength = PayloadLength + 1;

    // ---- VIA command ids ----
    private const byte CmdGetProtocolVersion = 0x01;
    private const byte CmdCustomSetValue = 0x07;
    private const byte CmdCustomGetValue = 0x08;
    private const byte Unhandled = 0xFF;

    // ---- VIA channel ids ----
    private const byte ChannelRgbLight = 2;
    private const byte ChannelRgbMatrix = 3;

    // ---- VIA value ids (same numbering for both lighting channels) ----
    private const byte ValueBrightness = 1;
    private const byte ValueEffect = 2;
    private const byte ValueColor = 4;

    /// <summary>
    /// Channel-based lighting commands arrived in VIA protocol 11. Earlier firmware used
    /// a different, incompatible layout, so those are reported rather than guessed at.
    /// </summary>
    private const int MinimumProtocolVersion = 11;

    /// <summary>QMK's RGB Matrix effect list puts "solid colour" at index 1 (0 is off).</summary>
    private const byte EffectSolidColor = 1;

    /// <summary>How often solid mode is re-sent. Long enough to be free, short enough to notice.</summary>
    private static readonly TimeSpan SolidModeInterval = TimeSpan.FromSeconds(5);

    private sealed class ViaState
    {
        public required SafeFileHandleEx Handle { get; init; }
        public required byte Channel { get; init; }
        public required int ProtocolVersion { get; init; }
        public Rgb24 LastSent = new(1, 2, 3);   // deliberately unlikely, forces a first write
        public bool Broken;

        /// <summary>
        /// When solid mode was last asserted.
        ///
        /// It is re-sent every so often rather than once, because the effect keys on the
        /// board itself still work while the app is running. Someone who bumps one drops
        /// the keyboard back into its own animation, and setting a colour we already
        /// think is current would otherwise never bring it back.
        /// </summary>
        public DateTime SolidModeSetAt = DateTime.MinValue;
    }

    private readonly List<ViaState> _open = new();

    public string Id => "native.via";
    public string DisplayName => "VIA keyboards";
    public string Description =>
        "Keyboards that speak the VIA protocol, including the QMK-based Sharkoon SKILLER models.";
    public bool IsAvailable => true;
    public string UnavailableReason => "";

    public Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<LightDevice>();
        CloseAll();

        List<HidNative.HidCollection> all;
        try { all = HidNative.Enumerate(); }
        catch { return Task.FromResult<IReadOnlyList<LightDevice>>(found); }

        foreach (var col in all)
        {
            if (ct.IsCancellationRequested) break;
            if (col.UsagePage != UsagePageRawHid || col.Usage != UsageRawHid) continue;

            var handle = HidNative.CreateFile(col.Path,
                HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) continue;

            int version = QueryProtocolVersion(handle);
            if (version < MinimumProtocolVersion)
            {
                handle.Dispose();
                continue;
            }

            // Find out which lighting system the firmware actually has.
            byte? channel = DetectLightingChannel(handle);
            if (channel is null)
            {
                handle.Dispose();
                continue;
            }

            var state = new ViaState
            {
                Handle = handle,
                Channel = channel.Value,
                ProtocolVersion = version,
            };
            _open.Add(state);

            string name = string.IsNullOrWhiteSpace(col.Product) ? "VIA keyboard" : col.Product;
            string lighting = channel.Value == ChannelRgbMatrix ? "RGB Matrix" : "RGB Light";

            found.Add(new LightDevice
            {
                Key = $"native.via:{col.VendorId:X4}:{col.ProductId:X4}",
                Name = name,
                ProviderId = Id,
                Role = DeviceRole.Keyboard,

                // VIA gives one colour for the whole board, so one light is the honest
                // representation.
                Zones = LightDevice.StripZones(1, "Keyboard", 440, 140),
                Details = $"VIA protocol {version}  -  {lighting}  -  USB {col.VendorId:X4}:{col.ProductId:X4}",
                Width = 440,
                Height = 140,
                Tag = state,

                // Three short HID reports per colour change; no reason to run flat out.
                MaxUpdatesPerSecond = 30,
                DefaultMaxUpdatesPerSecond = 30,
            });
        }

        return Task.FromResult<IReadOnlyList<LightDevice>>(found);
    }

    /// <summary>
    /// Asks for the VIA protocol version. Read-only; returns 0 if there is no answer.
    ///
    /// Tried more than once because a board that has just enumerated, or one whose own
    /// software has only recently let go of it, drops the first request often enough to
    /// matter. A single miss used to mean the keyboard was skipped entirely and the user
    /// had to press Rescan until it happened to land.
    /// </summary>
    private static int QueryProtocolVersion(SafeFileHandleEx handle)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var request = new byte[BufferLength];
                request[1] = CmdGetProtocolVersion;

                if (!HidNative.WriteFile(handle, request, request.Length, out _, IntPtr.Zero)) continue;

                var reply = HidNative.ReadWithTimeout(handle, BufferLength, 400);
                if (reply is null) continue;

                // The reply echoes the command, then the version as big-endian.
                if (reply[0] != CmdGetProtocolVersion) continue;

                int version = (reply[1] << 8) | reply[2];
                if (version > 0) return version;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VIA] version probe failed: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Works out whether the firmware exposes RGB Matrix or the simpler RGB Light, by
    /// reading a value from each. VIA answers an unsupported request with 0xFF.
    /// </summary>
    private static byte? DetectLightingChannel(SafeFileHandleEx handle)
    {
        foreach (byte channel in new[] { ChannelRgbMatrix, ChannelRgbLight })
        {
            try
            {
                var request = new byte[BufferLength];
                request[1] = CmdCustomGetValue;
                request[2] = channel;
                request[3] = ValueBrightness;

                if (!HidNative.WriteFile(handle, request, request.Length, out _, IntPtr.Zero)) continue;

                var reply = HidNative.ReadWithTimeout(handle, BufferLength, 400);
                if (reply is null) continue;

                // reply[0] is the command echo; 0xFF means "I do not support that".
                if (reply[0] == Unhandled) continue;
                if (reply[0] == CmdCustomGetValue && reply[1] == channel) return channel;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VIA] channel probe failed: {ex.Message}");
            }
        }

        return null;
    }

    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors)
    {
        if (device.Tag is not ViaState state || state.Broken || zoneColors.Length == 0) return false;

        var colour = zoneColors[0];
        bool reassert = DateTime.UtcNow - state.SolidModeSetAt > SolidModeInterval;

        if (colour == state.LastSent && !reassert) return true;

        // VIA separates hue/saturation from brightness, so the colour has to be converted.
        var (h, s, v) = RgbF.From(colour).ToHsv();
        byte hue = (byte)Math.Clamp(Math.Round(h * 255.0), 0, 255);
        byte sat = (byte)Math.Clamp(Math.Round(s * 255.0), 0, 255);
        byte val = (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);

        try
        {
            // Put the board into solid-colour mode, otherwise its own animation keeps
            // overwriting whatever colour we set.
            if (reassert)
            {
                if (!SetValue(state, ValueEffect, EffectSolidColor)) { state.Broken = true; return false; }
                state.SolidModeSetAt = DateTime.UtcNow;
            }

            if (!SetValue(state, ValueColor, hue, sat)) { state.Broken = true; return false; }
            if (!SetValue(state, ValueBrightness, val)) { state.Broken = true; return false; }

            state.LastSent = colour;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VIA] write failed: {ex.Message}");
            state.Broken = true;
            return false;
        }
    }

    private static bool SetValue(ViaState state, byte valueId, params byte[] data)
    {
        var buffer = new byte[BufferLength];
        buffer[1] = CmdCustomSetValue;
        buffer[2] = state.Channel;
        buffer[3] = valueId;
        for (int i = 0; i < data.Length && 4 + i < BufferLength; i++) buffer[4 + i] = data[i];

        return HidNative.WriteFile(state.Handle, buffer, buffer.Length, out _, IntPtr.Zero);
    }

    public void Release(LightDevice device)
    {
        // Turn the lighting off rather than leaving the last frame burnt in. Nothing is
        // saved to the keyboard, so its own settings return after a replug.
        if (device.Tag is not ViaState state || state.Broken) return;
        try { SetValue(state, ValueBrightness, 0); } catch { /* on the way out anyway */ }
    }

    private void CloseAll()
    {
        foreach (var s in _open) s.Handle.Dispose();
        _open.Clear();
    }

    public void Dispose() => CloseAll();
}
