using System.Diagnostics;
using SickRGB.Core;

namespace SickRGB.Hardware;

/// <summary>
/// Talks to a Turtle Beach / ROCCAT Magma keyboard.
///
/// Protocol (verified against a Turtle Beach Magma, firmware 1.08):
///   * Control collection  - usage page 0xFF01, feature reports.
///   * LED collection      - usage page 0xFF00, 65-byte output reports.
///
/// Enable direct mode:  feature report  0E 05 &lt;on&gt; 00 00
/// Push colours:        output report   00 A1 01 40 | R0..R4 | G0..G4 | B0..B4 | zero padding
///
/// The colour payload is planar: all five red bytes, then all five green, then
/// all five blue. Zone 0 is the leftmost strip.
/// </summary>
public sealed class MagmaDevice : IDisposable
{
    public const int ZoneCount = 5;

    private const ushort UsagePageControl = 0xFF01;
    private const ushort UsagePageLed = 0xFF00;
    private const int LedReportLength = 65;

    /// <summary>Devices known to speak this exact protocol.</summary>
    private static readonly (ushort Vid, ushort Pid, string Name)[] Supported =
    {
        (0x10F5, 0x5024, "Turtle Beach Magma"),   // verified on real hardware
        (0x1E7D, 0x3124, "ROCCAT Magma"),         // same protocol, original branding
        (0x1E7D, 0x69A0, "ROCCAT Magma Mini"),
    };

    private SafeFileHandleEx? _ctrl;
    private SafeFileHandleEx? _led;
    private readonly byte[] _packet = new byte[LedReportLength];
    private readonly object _ioLock = new();

    public string ProductName { get; private set; } = "Magma";
    public string FirmwareVersion { get; private set; } = "?";
    public ushort VendorId { get; private set; }
    public ushort ProductId { get; private set; }
    public bool IsConnected { get; private set; }

    private MagmaDevice() { }

    /// <summary>Finds and opens the keyboard. Returns null when no supported device is present.</summary>
    public static MagmaDevice? Open()
    {
        List<HidNative.HidCollection> all;
        try { all = HidNative.Enumerate(); }
        catch { return null; }

        foreach (var (vid, pid, name) in Supported)
        {
            var mine = all.Where(c => c.VendorId == vid && c.ProductId == pid).ToList();
            if (mine.Count == 0) continue;

            var ctrlInfo = mine.FirstOrDefault(c => c.UsagePage == UsagePageControl);
            var ledInfo = mine.FirstOrDefault(c => c.UsagePage == UsagePageLed && c.OutputReportLength == LedReportLength)
                          ?? mine.FirstOrDefault(c => c.OutputReportLength == LedReportLength);

            if (ctrlInfo is null || ledInfo is null) continue;

            var ctrl = HidNative.CreateFile(ctrlInfo.Path, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                                            HidNative.FILE_SHARE_READ_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            var led = HidNative.CreateFile(ledInfo.Path, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                                           HidNative.FILE_SHARE_READ_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

            if (ctrl.IsInvalid || led.IsInvalid)
            {
                ctrl.Dispose();
                led.Dispose();
                continue;
            }

            var dev = new MagmaDevice
            {
                _ctrl = ctrl,
                _led = led,
                VendorId = vid,
                ProductId = pid,
                ProductName = string.IsNullOrWhiteSpace(ctrlInfo.Product) ? name : $"{ctrlInfo.Product}",
                IsConnected = true,
            };

            dev.FirmwareVersion = dev.ReadFirmwareVersion();
            dev.WaitUntilReady();
            dev.EnableDirect(true);
            return dev;
        }

        return null;
    }

    /// <summary>Reads feature report 0x09; byte 2 holds the firmware version as an integer (108 =&gt; "1.08").</summary>
    private string ReadFirmwareVersion()
    {
        if (_ctrl is null) return "?";
        try
        {
            var buf = new byte[9];
            buf[0] = 0x09;
            if (!HidNative.HidD_GetFeature(_ctrl, buf, buf.Length)) return "?";
            int raw = buf[2];
            return $"{raw / 100}.{raw % 100:00}";
        }
        catch { return "?"; }
    }

    /// <summary>Polls feature report 0x04 until the device reports ready (byte 1 == 1).</summary>
    private void WaitUntilReady(int maxAttempts = 40)
    {
        if (_ctrl is null) return;
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                var buf = new byte[9];
                buf[0] = 0x04;
                if (HidNative.HidD_GetFeature(_ctrl, buf, buf.Length) && buf[1] == 1) return;
            }
            catch { /* fall through to retry */ }
            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Turns software (direct) lighting control on or off. When off, the keyboard
    /// resumes whatever effect is stored in its onboard profile.
    /// </summary>
    public bool EnableDirect(bool on)
    {
        if (_ctrl is null) return false;
        lock (_ioLock)
        {
            try
            {
                byte[] buf = { 0x0E, 0x05, (byte)(on ? 1 : 0), 0x00, 0x00 };
                bool ok = HidNative.HidD_SetFeature(_ctrl, buf, buf.Length);
                if (ok) Thread.Sleep(20);
                return ok;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Pushes five zone colours to the keyboard. Zone 0 is the leftmost strip.
    /// Returns false if the write failed (usually means the device was unplugged).
    /// </summary>
    public bool SendZones(ReadOnlySpan<Rgb24> zones)
    {
        if (_led is null || zones.Length < ZoneCount) return false;

        lock (_ioLock)
        {
            Array.Clear(_packet);
            _packet[0] = 0x00;   // report id
            _packet[1] = 0xA1;   // direct colour command
            _packet[2] = 0x01;   // packet index, 1-based
            _packet[3] = 0x40;   // declared payload length (64)

            for (int i = 0; i < ZoneCount; i++)
            {
                _packet[4 + i] = zones[i].R;
                _packet[9 + i] = zones[i].G;
                _packet[14 + i] = zones[i].B;
            }

            try
            {
                if (HidNative.WriteFile(_led, _packet, LedReportLength, out int written, IntPtr.Zero) && written == LedReportLength)
                    return true;

                // Some stacks prefer the HID API over raw WriteFile.
                if (HidNative.HidD_SetOutputReport(_led, _packet, LedReportLength))
                    return true;

                IsConnected = false;
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MagmaDevice] write failed: {ex.Message}");
                IsConnected = false;
                return false;
            }
        }
    }

    /// <summary>Blanks all zones and hands lighting back to the keyboard's onboard profile.</summary>
    public void ReleaseToHardware()
    {
        try
        {
            Span<Rgb24> off = stackalloc Rgb24[ZoneCount];
            SendZones(off);
            EnableDirect(false);
        }
        catch { /* best effort during shutdown */ }
    }

    public void Dispose()
    {
        ReleaseToHardware();
        IsConnected = false;
        _ctrl?.Dispose();
        _led?.Dispose();
        _ctrl = null;
        _led = null;
    }
}
