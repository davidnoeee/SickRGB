using System.Diagnostics;
using SickRGB.Core;
using SickRGB.Hardware;

namespace SickRGB.Devices.Providers;

/// <summary>
/// Native HID++ 2.0 control for Logitech "Lightsync" mice.
///
/// EXPERIMENTAL and off by default. Unlike the Magma driver, this protocol has not
/// been verified against hardware here: Logitech G HUB holds the device, and sending
/// speculative colour writes to a mouse risks wearing its onboard profile flash. The
/// feature-discovery step below is read-only and safe, so a device only appears at all
/// if it genuinely reports the colour-effects feature.
///
/// Enable it from Settings if you want to try it, with G HUB closed.
/// </summary>
public sealed class LogitechProvider : ILightProvider
{
    private const ushort LogitechVid = 0x046D;
    private const ushort UsagePageHidPlusPlus = 0xFF00;

    /// <summary>HID++ 2.0 feature: COLOR_LED_EFFECTS.</summary>
    private const ushort FeatureColorLedEffects = 0x8070;

    /// <summary>HID++ 2.0 feature: RGB_EFFECTS (newer devices).</summary>
    private const ushort FeatureRgbEffects = 0x8071;

    private const byte ReportShort = 0x10;
    private const byte ReportLong = 0x11;
    private const int ShortLength = 7;
    private const int LongLength = 20;

    /// <summary>Device index for a directly attached (wired) device.</summary>
    private const byte DirectDeviceIndex = 0xFF;

    /// <summary>Software id, any non-zero nibble. Tags responses as ours.</summary>
    private const byte SoftwareId = 0x0E;

    private static readonly Dictionary<ushort, (string Name, int Zones)> Known = new()
    {
        [0xC08F] = ("Logitech G403 HERO", 2),
        [0xC080] = ("Logitech G303 Daedalus Apex", 2),
        [0xC085] = ("Logitech G Pro", 2),
        [0xC08C] = ("Logitech G Pro HERO", 2),
        [0xC092] = ("Logitech G203 Lightsync", 3),
        [0xC09D] = ("Logitech G203 Lightsync", 3),
    };

    /// <summary>Set from settings. Nothing is written to any Logitech device while false.</summary>
    public static bool Enabled { get; set; }

    private sealed class LogiState
    {
        public required SafeFileHandleEx Handle { get; init; }
        public required byte FeatureIndex { get; init; }
        public Rgb24[] LastSent = Array.Empty<Rgb24>();
        public DateTime LastWrite = DateTime.MinValue;
        public bool Broken;
    }

    private readonly List<LogiState> _open = new();

    public string Id => "native.logitech";
    public string DisplayName => "Logitech mice (experimental)";
    public string Description => "Logitech Lightsync mice, controlled directly. Close Logitech G HUB first, or it keeps hold of the mouse.";
    public bool IsAvailable => Enabled;
    public string UnavailableReason => Enabled ? "" : "Turn this on in Settings to try it.";

    public Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<LightDevice>();
        if (!Enabled) return Task.FromResult<IReadOnlyList<LightDevice>>(found);

        CloseAll();

        List<HidNative.HidCollection> all;
        try { all = HidNative.Enumerate(); }
        catch { return Task.FromResult<IReadOnlyList<LightDevice>>(found); }

        foreach (var col in all)
        {
            if (col.VendorId != LogitechVid) continue;
            if (col.UsagePage != UsagePageHidPlusPlus) continue;
            if (!Known.TryGetValue(col.ProductId, out var info)) continue;

            var handle = HidNative.CreateFile(col.Path,
                HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) continue;

            // Read-only capability probe. If the device does not advertise a colour
            // feature we leave it alone entirely.
            byte featureIndex = QueryFeatureIndex(handle, FeatureColorLedEffects);
            if (featureIndex == 0) featureIndex = QueryFeatureIndex(handle, FeatureRgbEffects);

            if (featureIndex == 0)
            {
                handle.Dispose();
                continue;
            }

            var state = new LogiState { Handle = handle, FeatureIndex = featureIndex };
            _open.Add(state);

            var zones = LightDevice.StripZones(info.Zones, "LED", 34, 60);

            found.Add(new LightDevice
            {
                Key = $"native.logitech:{col.VendorId:X4}:{col.ProductId:X4}",
                Name = info.Name,
                ProviderId = Id,
                Role = DeviceRole.Mouse,
                Zones = zones,
                Details = $"HID++ 2.0  -  feature index 0x{featureIndex:X2}  -  experimental",
                Width = 34 * info.Zones,
                Height = 60,
                Tag = state,
            });
        }

        return Task.FromResult<IReadOnlyList<LightDevice>>(found);
    }

    /// <summary>
    /// Asks the HID++ root feature (index 0) for the index of <paramref name="featureId"/>.
    /// This is a pure query - it changes nothing on the device. Returns 0 when unsupported.
    /// </summary>
    private static byte QueryFeatureIndex(SafeFileHandleEx handle, ushort featureId)
    {
        try
        {
            var request = new byte[ShortLength];
            request[0] = ReportShort;
            request[1] = DirectDeviceIndex;
            request[2] = 0x00;                                  // root feature
            request[3] = (byte)((0x0 << 4) | SoftwareId);        // getFeature()
            request[4] = (byte)(featureId >> 8);
            request[5] = (byte)(featureId & 0xFF);
            request[6] = 0x00;

            if (!HidNative.WriteFile(handle, request, request.Length, out _, IntPtr.Zero))
                return 0;

            // Responses can arrive as either report size; read whichever turns up.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var response = ReadWithTimeout(handle, LongLength, 250);
                if (response is null) return 0;

                // Ignore traffic that is not our reply.
                if (response[1] != DirectDeviceIndex) continue;
                if ((response[3] & 0x0F) != SoftwareId) continue;

                // 0x8F marks an error response.
                if (response[2] == 0xFF) return 0;

                return response[4];
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Logitech] feature query failed: {ex.Message}");
        }
        return 0;
    }

    /// <summary>Blocking HID read that gives up after <paramref name="timeoutMs"/>.</summary>
    private static byte[]? ReadWithTimeout(SafeFileHandleEx handle, int length, int timeoutMs)
    {
        var buffer = new byte[length];
        var task = Task.Run(() => HidNative.ReadFile(handle, buffer, buffer.Length, out _, IntPtr.Zero));

        if (!task.Wait(timeoutMs))
        {
            // Unblock the pending read so the handle stays usable.
            try { HidNative.CancelIoEx(handle, IntPtr.Zero); } catch { }
            return null;
        }

        return task.Result ? buffer : null;
    }

    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors)
    {
        if (!Enabled) return false;
        if (device.Tag is not LogiState state || state.Broken) return false;

        // Only write when something actually changed, and never faster than 25 Hz.
        // Logitech mice can persist colour state, so needless writes are worth avoiding.
        bool changed = state.LastSent.Length != zoneColors.Length;
        if (!changed)
        {
            for (int i = 0; i < zoneColors.Length; i++)
                if (state.LastSent[i] != zoneColors[i]) { changed = true; break; }
        }
        if (!changed) return true;
        if ((DateTime.UtcNow - state.LastWrite).TotalMilliseconds < 40) return true;

        for (int zone = 0; zone < zoneColors.Length; zone++)
        {
            var buf = new byte[LongLength];
            buf[0] = ReportLong;
            buf[1] = DirectDeviceIndex;
            buf[2] = state.FeatureIndex;
            buf[3] = (byte)((0x3 << 4) | SoftwareId);   // setLightSettings
            buf[4] = (byte)zone;
            buf[5] = 0x01;                              // static
            buf[6] = zoneColors[zone].R;
            buf[7] = zoneColors[zone].G;
            buf[8] = zoneColors[zone].B;
            buf[9] = 0x02;                              // static marker

            if (!HidNative.WriteFile(state.Handle, buf, buf.Length, out _, IntPtr.Zero))
            {
                state.Broken = true;
                return false;
            }
        }

        state.LastSent = zoneColors.ToArray();
        state.LastWrite = DateTime.UtcNow;
        return true;
    }

    public void Release(LightDevice device)
    {
        // Leave the mouse on whatever colour it currently shows; G HUB or the onboard
        // profile takes over again once it is restarted.
    }

    private void CloseAll()
    {
        foreach (var s in _open) s.Handle.Dispose();
        _open.Clear();
    }

    public void Dispose() => CloseAll();
}
