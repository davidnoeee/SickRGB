using System.Diagnostics;
using SickRGB.Core;
using SickRGB.Hardware;

namespace SickRGB.Devices.Providers;

/// <summary>
/// Drives keyboards built on the EVision lighting interface, a design licensed to a large
/// number of brands and sold under many names.
///
/// These boards are not VIA and never answer a VIA probe. They expose a separate vendor
/// collection on usage page 0xFF1C carrying 64 byte reports in both directions, and all
/// lighting goes through that. <see cref="ViaKeyboardProvider"/> only ever looks at QMK's
/// raw HID page (0xFF60), so without this driver these keyboards are invisible to the app
/// no matter how they are plugged in.
///
/// Detection is by capability rather than by USB ID, for the same reason as the VIA
/// driver: the vendor IDs involved are shared OEM identifiers covering unrelated hardware.
/// A candidate has to expose the 0xFF1C collection with the right report sizes, and is
/// then asked to read back its own stored colours. Something that answers that is speaking
/// this protocol. The probe reads; it changes nothing.
///
/// What the board can do: per key colour, 126 addressable positions on a 6 by 23 grid, so
/// waves and ripples cross it properly rather than lighting it as one block.
///
/// Nothing is written to the keyboard's flash. The protocol frames a stored change with a
/// begin packet (0x01) and an end packet (0x02); this driver sends neither, so the colour
/// packets in between land in the working buffer the firmware renders from and the board
/// returns to its own settings when unplugged.
/// </summary>
public sealed class EVisionKeyboardProvider : ILightProvider
{
    /// <summary>The vendor collection that carries lighting on every board in this family.</summary>
    private const ushort UsagePageLighting = 0xFF1C;

    /// <summary>Report length in both directions, including the leading report id byte.</summary>
    private const int PacketLength = 64;

    /// <summary>Report id every lighting packet carries.</summary>
    private const byte ReportId = 0x04;

    // ---- commands ----
    private const byte CmdSetParameter = 0x06;
    private const byte CmdReadCustomColour = 0x10;
    private const byte CmdWriteCustomColour = 0x11;

    /// <summary>Parameter slot 0 takes the whole mode block in one write.</summary>
    private const byte ParameterModeBlock = 0x00;

    /// <summary>The only mode that renders the colours we send rather than its own animation.</summary>
    private const byte ModeCustom = 0x14;

    /// <summary>Brightness is a 0 to 4 step, not a byte.</summary>
    private const byte BrightnessMax = 0x04;

    /// <summary>Colour bytes per packet. The firmware rejects anything larger.</summary>
    private const int MaxColourBytesPerPacket = 0x36;

    /// <summary>126 positions, three bytes each, which is exactly seven full packets.</summary>
    private const int LedCount = 126;
    private const int ColourBytes = LedCount * 3;

    /// <summary>
    /// Which of the 126 colour slots each physical key sits at, as a 6 by 23 grid.
    ///
    /// The slots are not contiguous: the firmware reserves gaps for keys this layout does
    /// not have, and those are simply never written. -1 marks a hole in the grid.
    /// </summary>
    private static readonly int[,] KeyGrid =
    {
        {   0,  -1,   1,   2,   3,   4,  -1,   5,   6,   7,   8,  -1,   9,  10,  11,  12,  14,  15,  16,  -1,  -1,  -1,  -1 },
        {  21,  22,  23,  24,  25,  26,  27,  28,  29,  30,  31,  -1,  32,  33,  34,  -1,  35,  36,  37,  38,  39,  40,  41 },
        {  42,  -1,  43,  44,  45,  46,  -1,  47,  48,  49,  50,  51,  52,  53,  54,  55,  56,  57,  58,  59,  60,  61,  62 },
        {  63,  -1,  64,  65,  66,  67,  -1,  68,  69,  70,  71,  72,  73,  74,  76,  -1,  -1,  -1,  -1,  80,  81,  82,  -1 },
        {  84,  -1,  86,  87,  88,  89,  -1,  90,  -1,  91,  92,  93,  94,  95,  97,  -1,  -1,  99,  -1, 101, 102, 103, 104 },
        { 105, 106, 107,  -1,  -1,  -1,  -1, 108,  -1,  -1,  -1,  -1, 109, 110, 111, 113, 119, 120, 121, 123,  -1, 124,  -1 },
    };

    private const int GridRows = 6;
    private const int GridColumns = 23;

    /// <summary>Key size on the canvas, chosen so the whole board lands near keyboard scale.</summary>
    private const double KeySize = 19;
    private const double KeyPitch = 21;

    private sealed class BoardState
    {
        public required SafeFileHandleEx Handle { get; init; }

        /// <summary>Colour slot for each zone, in zone order.</summary>
        public required int[] SlotForZone { get; init; }

        /// <summary>The full 378 byte colour block, reused every frame.</summary>
        public readonly byte[] Colours = new byte[ColourBytes];

        /// <summary>One packet buffer, reused so a frame allocates nothing.</summary>
        public readonly byte[] Packet = new byte[PacketLength];

        public bool CustomModeSet;
        public bool Broken;

        /// <summary>Skips the write when a frame is identical to the one before it.</summary>
        public bool HasSent;
    }

    private readonly List<BoardState> _open = new();

    public string Id => "native.evision";
    public string DisplayName => "Vendor RGB keyboards";
    public string Description =>
        "Keyboards that carry their lighting on a vendor interface rather than VIA, driven directly and per key.";
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

            // The signature of the lighting interface: this vendor page, 64 bytes each way.
            if (col.UsagePage != UsagePageLighting) continue;
            if (col.OutputReportLength != PacketLength || col.InputReportLength != PacketLength) continue;

            var handle = HidNative.CreateFile(col.Path,
                HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) continue;

            bool answered = ProbeReadsBack(handle);

            var (zones, slots) = BuildKeyZones();

            var state = new BoardState { Handle = handle, SlotForZone = slots };
            _open.Add(state);

            string name = string.IsNullOrWhiteSpace(col.Product) ? "Vendor keyboard" : col.Product;

            found.Add(new LightDevice
            {
                Key = $"native.evision:{col.VendorId:X4}:{col.ProductId:X4}",
                Name = name,
                ProviderId = Id,
                Role = DeviceRole.Keyboard,
                Zones = zones,

                Details = answered
                    ? $"Per key lighting  -  {zones.Count} keys  -  USB {col.VendorId:X4}:{col.ProductId:X4}"
                    : $"Per key lighting, not confirmed by the board  -  {zones.Count} keys  -  USB {col.VendorId:X4}:{col.ProductId:X4}",

                Width = GridColumns * KeyPitch,
                Height = GridRows * KeyPitch,
                Tag = state,

                // A frame is seven packets down one interrupt endpoint. Past about thirty
                // a second the writes start queueing behind each other and the lighting
                // lags what is on screen rather than getting smoother.
                MaxUpdatesPerSecond = 30,
                DefaultMaxUpdatesPerSecond = 30,
            });
        }

        return Task.FromResult<IReadOnlyList<LightDevice>>(found);
    }

    /// <summary>
    /// Asks the board to read its stored colours back.
    ///
    /// Read-only, and used only to tell a board that speaks this protocol from an unrelated
    /// device that happens to share the vendor page. A board that stays silent is still
    /// adopted, because the page and report sizes together are already a narrow signature
    /// and some firmware answers nothing until it has been written to; the device detail
    /// line says which of the two happened.
    /// </summary>
    private static bool ProbeReadsBack(SafeFileHandleEx handle)
    {
        try
        {
            var packet = new byte[PacketLength];
            packet[0] = ReportId;
            packet[3] = CmdReadCustomColour;
            packet[4] = 0;                  // length
            packet[5] = 0;                  // offset low
            packet[6] = 0;                  // offset high
            Checksum(packet);

            if (!HidNative.WriteFile(handle, packet, packet.Length, out _, IntPtr.Zero)) return false;

            var reply = HidNative.ReadWithTimeout(handle, PacketLength, 350);
            return reply is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EVision] read-back probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Turns the key grid into zones laid out the way the keys physically sit, and records
    /// which colour slot each one writes to.
    /// </summary>
    private static (List<LightZone> Zones, int[] Slots) BuildKeyZones()
    {
        var zones = new List<LightZone>();
        var slots = new List<int>();

        for (int row = 0; row < GridRows; row++)
        {
            for (int column = 0; column < GridColumns; column++)
            {
                int slot = KeyGrid[row, column];
                if (slot < 0) continue;

                zones.Add(new LightZone
                {
                    Name = $"Key {zones.Count + 1}",
                    Index = zones.Count,
                    LocalX = column * KeyPitch,
                    LocalY = row * KeyPitch,
                    Width = KeySize,
                    Height = KeySize,
                });

                slots.Add(slot);
            }
        }

        return (zones, slots.ToArray());
    }

    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors)
    {
        if (device.Tag is not BoardState state || state.Broken) return false;

        int count = Math.Min(zoneColors.Length, state.SlotForZone.Length);
        if (count == 0) return false;

        bool changed = !state.HasSent;

        for (int i = 0; i < count; i++)
        {
            int offset = state.SlotForZone[i] * 3;
            var colour = zoneColors[i];

            if (state.Colours[offset] != colour.R
             || state.Colours[offset + 1] != colour.G
             || state.Colours[offset + 2] != colour.B)
            {
                state.Colours[offset] = colour.R;
                state.Colours[offset + 1] = colour.G;
                state.Colours[offset + 2] = colour.B;
                changed = true;
            }
        }

        if (!changed) return true;

        try
        {
            // The board runs its own animation until it is told to render what we send,
            // and that only has to be said once.
            if (!state.CustomModeSet)
            {
                if (!SendMode(state, ModeCustom, BrightnessMax)) { state.Broken = true; return false; }
                state.CustomModeSet = true;
            }

            if (!SendColours(state)) { state.Broken = true; return false; }

            state.HasSent = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EVision] write failed: {ex.Message}");
            state.Broken = true;
            return false;
        }
    }

    /// <summary>Sends the whole colour block as seven back to back packets.</summary>
    private static bool SendColours(BoardState state)
    {
        for (int sent = 0; sent < ColourBytes; sent += MaxColourBytesPerPacket)
        {
            int size = Math.Min(MaxColourBytesPerPacket, ColourBytes - sent);

            var packet = state.Packet;
            Array.Clear(packet);

            packet[0] = ReportId;
            packet[3] = CmdWriteCustomColour;
            packet[4] = (byte)size;
            packet[5] = (byte)(sent & 0xFF);
            packet[6] = (byte)(sent >> 8);

            Buffer.BlockCopy(state.Colours, sent, packet, 8, size);
            Checksum(packet);

            if (!HidNative.WriteFile(state.Handle, packet, packet.Length, out _, IntPtr.Zero)) return false;
        }

        return true;
    }

    /// <summary>
    /// Sets the whole mode block at once: mode, brightness, speed, direction, the random
    /// colour flag, and the colour the built-in effects use.
    /// </summary>
    private static bool SendMode(BoardState state, byte mode, byte brightness)
    {
        var packet = state.Packet;
        Array.Clear(packet);

        packet[0] = ReportId;
        packet[3] = CmdSetParameter;
        packet[4] = 8;                      // parameter length
        packet[5] = ParameterModeBlock;

        packet[8] = mode;
        packet[9] = brightness;
        packet[10] = 0;                     // speed, unused while we drive every key
        packet[11] = 0;                     // direction, likewise
        packet[12] = 0;                     // random colour off
        packet[13] = 0;                     // the effect colour, unused in custom mode
        packet[14] = 0;
        packet[15] = 0;

        Checksum(packet);

        return HidNative.WriteFile(state.Handle, packet, packet.Length, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Sums the packet body into the two checksum bytes.
    ///
    /// The bytes are added as signed values, which looks wrong and is not: the reference
    /// implementation every board in this family was tested against accumulates a signed
    /// char, so any colour byte above 0x7F contributes a negative number. Adding them as
    /// unsigned would produce a different checksum for most frames and firmware that does
    /// check would reject them.
    /// </summary>
    private static void Checksum(byte[] packet)
    {
        int sum = 0;
        for (int i = 3; i < PacketLength; i++) sum += (sbyte)packet[i];

        packet[1] = (byte)(sum & 0xFF);
        packet[2] = (byte)((sum >> 8) & 0xFF);
    }

    public void Release(LightDevice device)
    {
        if (device.Tag is not BoardState state || state.Broken) return;

        try
        {
            // Black rather than the last frame, so closing the app does not leave a still
            // image burnt across the keys. Nothing was saved, so the board's own lighting
            // comes back when it is replugged.
            Array.Clear(state.Colours);
            SendColours(state);
        }
        catch { /* on the way out anyway */ }
    }

    private void CloseAll()
    {
        foreach (var s in _open) s.Handle.Dispose();
        _open.Clear();
    }

    public void Dispose() => CloseAll();
}
