using System.Runtime.InteropServices;
using System.Text;

namespace SickRGB.Hardware;

/// <summary>
/// Gathers everything needed to write a driver for an unsupported device.
///
/// Writing a lighting driver blind needs four things, and this collects all of them:
///
///  1. Identity: which USB interface and collection a vendor channel actually lives on.
///  2. Report structure: the report IDs, sizes, usages and value ranges the device
///     declares. Windows does not hand out the raw report descriptor, but the parsed caps
///     carry the same information, which is what a driver has to match.
///  3. Feature report contents: firmware versions, state and capability blobs. Reading
///     these is how the Magma protocol was worked out, and it changes nothing on the
///     device.
///  4. What the device sends while you use it, which shows which collection carries
///     vendor traffic as opposed to ordinary key presses.
///
/// Everything here is read-only. Nothing is written to any device.
/// </summary>
internal static class HidDiagnostics
{
    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin, LogicalMax;
        public int PhysicalMin, PhysicalMax;

        // Union tail: usage range when IsRange, otherwise a single usage.
        public ushort U0, U1, U2, U3, U4, U5, U6, U7;

        public readonly ushort UsageMin => U0;
        public readonly ushort UsageMax => U1;
        public readonly ushort Usage => U0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public uint[] Reserved;

        public ushort U0, U1, U2, U3, U4, U5, U6, U7;

        public readonly ushort UsageMin => U0;
        public readonly ushort UsageMax => U1;
        public readonly ushort Usage => U0;
    }

    private const int HidP_Input = 0;
    private const int HidP_Output = 1;
    private const int HidP_Feature = 2;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [In, Out] HIDP_VALUE_CAPS[] caps,
                                                ref ushort capsLength, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, [In, Out] HIDP_BUTTON_CAPS[] caps,
                                                 ref ushort capsLength, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandleEx h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HidNative.HIDP_CAPS caps);

    // ---------------------------------------------------------------- report

    /// <summary>
    /// Builds the full diagnostic report. <paramref name="vendorFilter"/> limits it to one
    /// USB vendor, which keeps the output readable when a PC has a dozen HID devices.
    /// </summary>
    public static string Build(ushort? vendorFilter = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("SickRGB device report");
        sb.AppendLine("=====================");
        sb.AppendLine();
        AppendSystem(sb);

        List<HidNative.HidCollection> all;
        try
        {
            all = HidNative.Enumerate();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Could not enumerate HID devices: {ex.Message}");
            return sb.ToString();
        }

        var wanted = vendorFilter is { } vid
            ? all.Where(c => c.VendorId == vid).ToList()
            : all;

        sb.AppendLine($"HID collections: {wanted.Count} shown, {all.Count} present in total");
        sb.AppendLine();

        // A quick index first, so the interesting interface is easy to spot.
        sb.AppendLine("Summary");
        sb.AppendLine("-------");
        foreach (var c in wanted.OrderBy(c => c.VendorId).ThenBy(c => c.ProductId).ThenBy(c => c.UsagePage))
        {
            sb.AppendLine($"  {c.VendorId:X4}:{c.ProductId:X4}  usage {c.UsagePage:X4}/{c.Usage:X2}" +
                          $"  in {c.InputReportLength,4}  out {c.OutputReportLength,4}  feat {c.FeatureReportLength,4}" +
                          $"   {Describe(c)}");
        }
        sb.AppendLine();

        foreach (var collection in wanted)
        {
            AppendCollection(sb, collection);
        }

        sb.AppendLine();
        sb.AppendLine("End of report.");
        return sb.ToString();
    }

    private static string Describe(HidNative.HidCollection c) => c.UsagePage switch
    {
        0x0001 when c.Usage == 0x06 => "standard keyboard",
        0x0001 when c.Usage == 0x02 => "mouse",
        0x0001 when c.Usage == 0x80 => "system control",
        0x000C => "consumer control (media keys)",
        0xFF60 => "QMK raw HID (VIA)",
        >= 0xFF00 => "VENDOR DEFINED  <-- lighting usually lives here",
        _ => "",
    };

    private static void AppendSystem(StringBuilder sb)
    {
        sb.AppendLine("System");
        sb.AppendLine("------");
        sb.AppendLine($"  Windows        : {Environment.OSVersion.Version} (build {Environment.OSVersion.Version.Build})");
        sb.AppendLine($"  64-bit process : {Environment.Is64BitProcess}");

        bool elevated = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        sb.AppendLine($"  Elevated       : {elevated}");

        // Other RGB software will hold devices open and make everything here look broken.
        var rivals = new[] { "OpenRGB", "iCUE", "MysticLight", "Aura", "lghub", "SignalRgb",
                             "Synapse", "ROCCAT", "Swarm", "Turtle Beach", "Sharkoon" };
        var running = new List<string>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                if (rivals.Any(r => p.ProcessName.Contains(r, StringComparison.OrdinalIgnoreCase)))
                    running.Add(p.ProcessName);
            }
        }
        catch { }

        sb.AppendLine(running.Count > 0
            ? $"  Other RGB apps : {string.Join(", ", running.Distinct())}  <-- these can hold a device open"
            : "  Other RGB apps : none detected");
        sb.AppendLine();
    }

    private static void AppendCollection(StringBuilder sb, HidNative.HidCollection c)
    {
        sb.AppendLine("--------------------------------------------------------------------");
        sb.AppendLine($"{c.VendorId:X4}:{c.ProductId:X4}  usage page 0x{c.UsagePage:X4}, usage 0x{c.Usage:X2}   {Describe(c)}");
        sb.AppendLine($"  product      : {c.Product}");
        sb.AppendLine($"  manufacturer : {c.Manufacturer}");
        sb.AppendLine($"  serial       : {c.Serial}");
        sb.AppendLine($"  release      : 0x{c.VersionNumber:X4}");
        sb.AppendLine($"  reports      : input {c.InputReportLength}, output {c.OutputReportLength}, feature {c.FeatureReportLength}");
        sb.AppendLine($"  path         : {c.Path}");

        using var handle = HidNative.CreateFile(c.Path, 0, HidNative.FILE_SHARE_READ_WRITE,
                                                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            sb.AppendLine("  (could not open for inspection)");
            sb.AppendLine();
            return;
        }

        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                AppendCaps(sb, preparsed);
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        // Feature reports carry firmware versions and state. Reading them is harmless, and
        // only worth doing on vendor channels; the standard ones are already documented.
        if (c.UsagePage >= 0xFF00 && c.FeatureReportLength > 0)
            AppendFeatureProbe(sb, c);

        if (c.UsagePage == 0xFF60)
            AppendViaProbe(sb, c);

        sb.AppendLine();
    }

    /// <summary>
    /// Dumps the parsed report structure. This is the closest thing Windows gives to the
    /// device's report descriptor, and it is what a driver has to match byte for byte.
    /// </summary>
    private static void AppendCaps(StringBuilder sb, IntPtr preparsed)
    {
        var caps = new HidNative.HIDP_CAPS();
        if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return;

        foreach (var (type, name, valueCount, buttonCount) in new[]
                 {
                     (HidP_Input,   "input",   caps.NumberInputValueCaps,   caps.NumberInputButtonCaps),
                     (HidP_Output,  "output",  caps.NumberOutputValueCaps,  caps.NumberOutputButtonCaps),
                     (HidP_Feature, "feature", caps.NumberFeatureValueCaps, caps.NumberFeatureButtonCaps),
                 })
        {
            if (valueCount == 0 && buttonCount == 0) continue;
            sb.AppendLine($"  {name} items:");

            if (buttonCount > 0)
            {
                var buttons = new HIDP_BUTTON_CAPS[buttonCount];
                ushort length = buttonCount;
                if (HidP_GetButtonCaps(type, buttons, ref length, preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < length; i++)
                    {
                        var b = buttons[i];
                        string usage = b.IsRange != 0
                            ? $"usages 0x{b.UsageMin:X2}-0x{b.UsageMax:X2}"
                            : $"usage 0x{b.Usage:X2}";
                        sb.AppendLine($"    buttons  report 0x{b.ReportID:X2}  page 0x{b.UsagePage:X4}  {usage}");
                    }
                }
            }

            if (valueCount > 0)
            {
                var values = new HIDP_VALUE_CAPS[valueCount];
                ushort length = valueCount;
                if (HidP_GetValueCaps(type, values, ref length, preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < length; i++)
                    {
                        var v = values[i];
                        string usage = v.IsRange != 0
                            ? $"usages 0x{v.UsageMin:X2}-0x{v.UsageMax:X2}"
                            : $"usage 0x{v.Usage:X2}";
                        sb.AppendLine($"    values   report 0x{v.ReportID:X2}  page 0x{v.UsagePage:X4}  {usage}" +
                                      $"  {v.ReportCount} x {v.BitSize} bits  logical {v.LogicalMin}..{v.LogicalMax}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads feature reports across the low report IDs and prints whatever comes back.
    /// Purely a read; nothing is sent that could change device state.
    /// </summary>
    private static void AppendFeatureProbe(StringBuilder sb, HidNative.HidCollection c)
    {
        sb.AppendLine("  feature reports that answer a read:");

        using var handle = HidNative.CreateFile(c.Path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            sb.AppendLine("    (device is held open by something else, or refused read access)");
            return;
        }

        int length = Math.Min(Math.Max(c.FeatureReportLength, 9), 64);
        bool any = false;

        for (int id = 0x00; id <= 0x1F; id++)
        {
            var buffer = new byte[length];
            buffer[0] = (byte)id;

            bool ok;
            try { ok = HidNative.HidD_GetFeature(handle, buffer, buffer.Length); }
            catch { continue; }

            if (!ok) continue;

            // An all-zero answer usually means "no such report" rather than real data.
            if (buffer.Skip(1).All(b => b == 0)) continue;

            any = true;
            sb.AppendLine($"    0x{id:X2}: {Hex(buffer, 32)}");
        }

        if (!any) sb.AppendLine("    (none answered with data)");
    }

    /// <summary>Asks a QMK raw-HID endpoint what it supports. Read-only.</summary>
    private static void AppendViaProbe(StringBuilder sb, HidNative.HidCollection c)
    {
        sb.AppendLine("  VIA probe:");

        using var handle = HidNative.CreateFile(c.Path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            sb.AppendLine("    (could not open)");
            return;
        }

        try
        {
            var request = new byte[33];
            request[1] = 0x01;   // id_get_protocol_version
            if (!HidNative.WriteFile(handle, request, request.Length, out _, IntPtr.Zero))
            {
                sb.AppendLine("    (write failed)");
                return;
            }

            var reply = HidNative.ReadWithTimeout(handle, 33, 500);
            if (reply is null)
            {
                sb.AppendLine("    (no reply)");
                return;
            }

            sb.AppendLine($"    raw reply       : {Hex(reply, 16)}");
            if (reply[0] == 0x01)
                sb.AppendLine($"    protocol version: {(reply[1] << 8) | reply[2]}");

            // Which lighting system the firmware exposes, if any.
            foreach (var (channel, label) in new[] { ((byte)3, "RGB Matrix"), ((byte)2, "RGB Light") })
            {
                var query = new byte[33];
                query[1] = 0x08;      // id_custom_get_value
                query[2] = channel;
                query[3] = 0x01;      // brightness

                if (!HidNative.WriteFile(handle, query, query.Length, out _, IntPtr.Zero)) continue;
                var answer = HidNative.ReadWithTimeout(handle, 33, 500);
                if (answer is null) continue;

                string verdict = answer[0] == 0xFF ? "not supported" : $"supported ({Hex(answer, 8)})";
                sb.AppendLine($"    channel {channel} ({label}): {verdict}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"    probe failed: {ex.Message}");
        }
    }

    public static string Hex(byte[] data, int max)
    {
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        if (data.Length > n) sb.Append("...");
        return sb.ToString().TrimEnd();
    }
}
