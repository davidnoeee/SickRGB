using System.Runtime.InteropServices;
using System.Text;

namespace SickRGB.Hardware;

/// <summary>
/// Gathers everything needed to write a lighting driver for a device nobody involved owns.
///
/// The test this has to pass: someone pastes the output into a chat, and a developer on the
/// other end can write a working driver from it without ever touching the hardware. That
/// takes more than a list of devices.
///
/// Five layers, roughly in order of how much they unblock:
///
///  1. USB. Which interfaces exist, what class each one claims, and above all which
///     endpoints each one has. Whether an interface owns an interrupt OUT endpoint decides
///     whether a driver can write to it directly or has to go down the control pipe, and
///     guessing that wrong is the difference between a keyboard that lights up and one that
///     silently ignores everything. Read from the hub the device is plugged into.
///  2. HID structure. The collection tree, every report id, and every field with its size,
///     range and behaviour flags. Windows will not hand over the raw report descriptor, but
///     the parsed form carries the same facts.
///  3. Feature reports. Firmware versions, capability blobs and stored state. Reading them
///     changes nothing and is how several protocols in this app were worked out.
///  4. Fingerprints. Which known protocol family the signature matches, and what that
///     implies about transport and packet shape.
///  5. Live traffic, captured separately and only when asked for.
///
/// Nothing here changes a device. Almost all of it is literally a read; the one exception
/// is the VIA probe, which has to send a request packet before the keyboard will answer,
/// and the packets it sends are the two VIA getters that ask for a version and a stored
/// value. No command that sets anything, and nothing that writes to a device's memory, is
/// sent by any part of this.
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

        // Union tail: a usage range when IsRange, otherwise a single usage, followed by
        // string and designator indices.
        public ushort U0, U1, U2, U3, U4, U5, U6, U7;

        public readonly ushort UsageMin => U0;
        public readonly ushort UsageMax => U1;
        public readonly ushort Usage => U0;
        public readonly ushort StringMin => U2;
        public readonly ushort StringMax => U3;
        public readonly ushort StringIndex => U2;
        public readonly ushort DesignatorMin => U4;
        public readonly ushort DesignatorIndex => U4;
        public readonly ushort DataIndexMin => U6;
        public readonly ushort DataIndexMax => U7;
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
        public readonly ushort DataIndexMin => U6;
        public readonly ushort DataIndexMax => U7;
    }

    /// <summary>
    /// One node of the collection tree.
    ///
    /// The trailing UserContext pointer is what fixes the size at 24 bytes on x64; leaving
    /// it out would make every node after the first read from the wrong offset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_LINK_COLLECTION_NODE
    {
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public ushort Parent;
        public ushort NumberOfChildren;
        public ushort NextSibling;
        public ushort FirstChild;

        /// <summary>Low byte is the collection type, next bit is IsAlias, rest reserved.</summary>
        public uint Bits;

        public IntPtr UserContext;

        public readonly byte CollectionType => (byte)(Bits & 0xFF);
        public readonly bool IsAlias => ((Bits >> 8) & 1) != 0;
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
    private static extern int HidP_GetLinkCollectionNodes([In, Out] HIDP_LINK_COLLECTION_NODE[] nodes,
                                                          ref uint nodeLength, IntPtr preparsed);

    // As in HidNative: HidD_ returns a one byte BOOLEAN, HidP_ returns a four byte NTSTATUS.
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandleEx h, out IntPtr preparsed);

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HidNative.HIDP_CAPS caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetIndexedString(SafeFileHandleEx h, uint index, byte[] buffer, int length);

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetNumInputBuffers(SafeFileHandleEx h, out uint number);

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetInputReport(SafeFileHandleEx h, byte[] buffer, int length);

    /// <summary>
    /// Fills a report buffer with the defaults for one report id.
    ///
    /// Used purely to ask whether a report id exists. It runs entirely inside hid.dll and
    /// sends nothing to the device, and unlike the caps arrays it finds report ids whose
    /// contents are all constant or padding. Vendor blob reports are frequently declared
    /// exactly that way, which is why the caps arrays alone miss them.
    /// </summary>
    [DllImport("hid.dll")]
    private static extern int HidP_InitializeReportForID(int reportType, byte reportId, IntPtr preparsed,
                                                         byte[] report, uint reportLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandleEx h, uint code, IntPtr input, int inputSize,
                                               byte[] output, int outputSize, ref int returned, IntPtr overlapped);

    /// <summary>
    /// IOCTL_HID_GET_COLLECTION_INFORMATION. The only way to learn how many bytes the
    /// preparsed data blob occupies; HidD_GetPreparsedData hands back a bare pointer.
    /// </summary>
    private const uint IoctlHidGetCollectionInformation = 0x000B01A8;

    private const int HIDP_STATUS_REPORT_DOES_NOT_EXIST = unchecked((int)0xC0110010);

    // ---------------------------------------------------------------- report

    /// <summary>
    /// Builds the full diagnostic report. <paramref name="vendorFilter"/> limits it to one
    /// USB vendor, which keeps the output readable when a PC has a dozen HID devices.
    /// </summary>
    public static string Build(ushort? vendorFilter = null)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, vendorFilter);
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

        var collections = vendorFilter is { } vid
            ? all.Where(c => c.VendorId == vid).ToList()
            : all;

        List<UsbTopology.UsbDevice> usb;
        try { usb = UsbTopology.Enumerate(vendorFilter); }
        catch (Exception ex)
        {
            usb = new List<UsbTopology.UsbDevice>();
            sb.AppendLine($"(USB layer unavailable: {ex.Message})");
            sb.AppendLine();
        }

        AppendIndex(sb, collections, all.Count, usb);

        // One block per physical device, with its USB facts and then its HID collections,
        // because that is the unit someone writes a driver against.
        var groups = collections
            .GroupBy(c => (c.VendorId, c.ProductId))
            .OrderBy(g => g.Key.VendorId).ThenBy(g => g.Key.ProductId);

        foreach (var group in groups)
        {
            var matching = usb.Where(u => u.VendorId == group.Key.VendorId
                                       && u.ProductId == group.Key.ProductId).ToList();

            AppendDevice(sb, group.Key.VendorId, group.Key.ProductId, group.ToList(), matching);
        }

        sb.AppendLine();
        sb.AppendLine("End of report.");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, ushort? vendorFilter)
    {
        string version = typeof(HidDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        sb.AppendLine("SickRGB device report");
        sb.AppendLine("=====================");
        sb.AppendLine($"SickRGB {version}   {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (vendorFilter is { } v) sb.AppendLine($"Filtered to USB vendor {v:X4}.");
        sb.AppendLine();

        sb.AppendLine("What this is");
        sb.AppendLine("------------");
        sb.AppendLine("  Everything Windows will say about the connected devices, gathered so that a");
        sb.AppendLine("  driver can be written for hardware the developer does not own.");
        sb.AppendLine();
        sb.AppendLine("  Every value here was read. Nothing was written to any device, and nothing that");
        sb.AppendLine("  could change how a device is configured was sent.");
        sb.AppendLine();
        sb.AppendLine("  Not included: what you type. This report contains no keystrokes. The separate");
        sb.AppendLine("  listening feature does see device traffic, says so before it starts, and saves");
        sb.AppendLine("  nothing on its own.");
        sb.AppendLine();
        sb.AppendLine("  Worth knowing before you share it: serial numbers appear below, and on some");
        sb.AppendLine("  hardware those are unique to your unit.");
        sb.AppendLine();
    }

    private static void AppendSystem(StringBuilder sb)
    {
        sb.AppendLine("System");
        sb.AppendLine("------");
        sb.AppendLine($"  Windows        : {Environment.OSVersion.Version} (build {Environment.OSVersion.Version.Build})");
        sb.AppendLine($"  Architecture   : {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
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

        // Other RGB software holds devices open and makes everything here look broken.
        var rivals = new[] { "OpenRGB", "iCUE", "MysticLight", "Aura", "lghub", "SignalRgb",
                             "Synapse", "ROCCAT", "Swarm", "Turtle Beach", "Sharkoon", "Corsair",
                             "Wooting", "SteelSeries", "GHUB", "Armoury", "FanControl" };
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
            ? $"  Other RGB apps : {string.Join(", ", running.Distinct().OrderBy(x => x))}"
            : "  Other RGB apps : none detected");

        if (running.Count > 0)
            sb.AppendLine("                   These hold devices open, which can make reads below fail.");

        sb.AppendLine();
    }

    private static void AppendIndex(StringBuilder sb, List<HidNative.HidCollection> shown, int total,
                                    List<UsbTopology.UsbDevice> usb)
    {
        sb.AppendLine("Index");
        sb.AppendLine("-----");
        sb.AppendLine($"  HID collections : {shown.Count} shown, {total} present");
        sb.AppendLine($"  USB devices read from their hub : {usb.Count}");
        sb.AppendLine();

        if (shown.Count == 0)
        {
            sb.AppendLine("  (nothing matched the filter)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("  vid:pid    usage      report bytes   where       what it looks like");
        sb.AppendLine("                        in  out  feat");
        sb.AppendLine("  ---------  ---------  --- ---- -----  ----------  ------------------");

        foreach (var c in shown.OrderBy(c => c.VendorId).ThenBy(c => c.ProductId)
                               .ThenBy(c => InterfaceNumber(c.Path) ?? 99).ThenBy(c => c.UsagePage))
        {
            string where = InterfaceNumber(c.Path) is { } n
                ? $"mi_{n:00}" + (CollectionNumber(c.Path) is { } k ? $" col{k:00}" : "")
                : "-";

            sb.AppendLine($"  {c.VendorId:X4}:{c.ProductId:X4}  {c.UsagePage:X4}/{c.Usage:X4}  " +
                          $"{c.InputReportLength,3} {c.OutputReportLength,4} {c.FeatureReportLength,5}  " +
                          $"{where,-10}  {Describe(c)}");
        }

        sb.AppendLine();
    }

    private static string Describe(HidNative.HidCollection c)
    {
        string top = HidUsageNames.TopLevel(c.UsagePage, c.Usage);
        if (top.Length > 0) return top;
        if (c.UsagePage >= 0xFF00) return "VENDOR DEFINED  <-- lighting usually lives here";
        return HidUsageNames.Page(c.UsagePage);
    }

    // ---------------------------------------------------------------- device block

    private static void AppendDevice(StringBuilder sb, ushort vid, ushort pid,
                                     List<HidNative.HidCollection> collections,
                                     List<UsbTopology.UsbDevice> usb)
    {
        var first = collections[0];
        string name = !string.IsNullOrWhiteSpace(first.Product) ? first.Product : "(no product string)";

        sb.AppendLine("====================================================================");
        sb.AppendLine($"DEVICE  {vid:X4}:{pid:X4}   {name}");
        sb.AppendLine("====================================================================");

        string vendorName = HidUsageNames.Vendor(vid);
        sb.AppendLine($"  manufacturer : {first.Manufacturer}");
        sb.AppendLine($"  product      : {first.Product}");
        sb.AppendLine($"  serial       : {(first.Serial.Length > 0 ? first.Serial : "(none)")}");
        sb.AppendLine($"  release      : 0x{first.VersionNumber:X4}");
        if (vendorName.Length > 0)
            sb.AppendLine($"  vendor id    : {vendorName}");
        sb.AppendLine();

        AppendUsb(sb, usb);
        AppendFingerprint(sb, vid, collections, usb);

        foreach (var c in collections.OrderBy(c => InterfaceNumber(c.Path) ?? 99)
                                     .ThenBy(c => CollectionNumber(c.Path) ?? 99))
        {
            AppendCollection(sb, c, usb);
        }
    }

    private static void AppendUsb(StringBuilder sb, List<UsbTopology.UsbDevice> usb)
    {
        sb.AppendLine("USB");
        sb.AppendLine("---");

        if (usb.Count == 0)
        {
            sb.AppendLine("  The hub would not describe this device, so interfaces and endpoints are");
            sb.AppendLine("  unknown. Everything below still applies; only the USB layer is missing.");
            sb.AppendLine();
            return;
        }

        if (usb.Count > 1)
            sb.AppendLine($"  {usb.Count} devices share this vendor and product id, so all are listed.");

        foreach (var d in usb)
        {
            sb.AppendLine($"  port {d.Port}, address {d.Address}, {d.SpeedName}, {d.StatusName}");
            sb.AppendLine($"  usb {Bcd(d.BcdUsb)}, device revision {Bcd(d.BcdDevice)}, " +
                          $"class 0x{d.DeviceClass:X2}/0x{d.DeviceSubClass:X2}/0x{d.DeviceProtocol:X2}, " +
                          $"ep0 {d.MaxPacketSize0} bytes, {d.NumConfigurations} configuration(s), {d.OpenPipes} open pipe(s)");

            if (d.Manufacturer.Length > 0 || d.Product.Length > 0 || d.Serial.Length > 0)
                sb.AppendLine($"  strings: manufacturer \"{d.Manufacturer}\", product \"{d.Product}\", serial \"{d.Serial}\"");

            if (d.Error is not null)
                sb.AppendLine($"  note: {d.Error}");

            sb.AppendLine();

            foreach (var i in d.Interfaces)
            {
                string alt = i.AlternateSetting > 0 ? $" alt {i.AlternateSetting}" : "";
                sb.AppendLine($"    interface {i.Number}{alt}: {i.ClassName}" +
                              $"  sub 0x{i.SubClass:X2}  proto 0x{i.Protocol:X2}" +
                              (i.HidProtocolName.Length > 0 ? $"  ({i.HidProtocolName})" : ""));

                if (i.Hid is { } hid)
                    sb.AppendLine($"      HID {Bcd(hid.BcdHid)}, country {hid.CountryCode}, " +
                                  $"report descriptor {hid.DescriptorLength} bytes");

                if (i.Endpoints.Count == 0)
                    sb.AppendLine("      no endpoints: everything must go through the control pipe");

                foreach (var e in i.Endpoints)
                    sb.AppendLine($"      endpoint {e}");

                // The single most useful line in the whole USB section.
                sb.AppendLine(i.HasInterruptOut
                    ? "      -> writable directly: this interface has an interrupt OUT endpoint"
                    : "      -> NOT directly writable: no interrupt OUT endpoint, so writes must use");
                if (!i.HasInterruptOut)
                    sb.AppendLine("         the control pipe (HidD_SetOutputReport or HidD_SetFeature)");

                sb.AppendLine();
            }

            if (d.RawDeviceDescriptor is { } dd)
                sb.AppendLine($"    device descriptor : {Hex(dd, dd.Length)}");
            if (d.RawConfigDescriptor is { } cd)
            {
                sb.AppendLine($"    configuration descriptor ({cd.Length} bytes):");
                AppendHexBlock(sb, cd, "      ");
            }

            sb.AppendLine();
        }
    }

    private static string Bcd(ushort value) => $"{value >> 8:X}.{value & 0xFF:X2}";

    // ---------------------------------------------------------------- fingerprint

    /// <summary>
    /// Says which known protocol family the device looks like, and what that implies.
    ///
    /// This is a hint, not a verdict. It exists because recognising the family is most of
    /// the work: once you know a board is EVision or QMK, the packet shape and the
    /// transport are already documented somewhere.
    /// </summary>
    private static void AppendFingerprint(StringBuilder sb, ushort vid,
                                          List<HidNative.HidCollection> collections,
                                          List<UsbTopology.UsbDevice> usb)
    {
        var notes = new List<string>();

        foreach (var c in collections)
        {
            switch (c.UsagePage)
            {
                case 0xFF60 when c.Usage == 0x61:
                    notes.Add("QMK raw HID on 0xFF60/0x61. VIA runs over this. 32 byte reports, " +
                              "command byte at offset 1 after the report id. Probe result below.");
                    break;

                case 0xFF1C:
                    notes.Add("EVision lighting interface on 0xFF1C. 64 byte reports, report id 0x04, " +
                              "checksum in bytes 1 and 2, command in byte 3. Per key custom mode is 0x14.");
                    break;

                case 0xFF00 when vid == 0x046D:
                    notes.Add("Logitech HID++ short reports on 0xFF00.");
                    break;
            }
        }

        if (vid is 0x320F or 0x0C45 or 0x3299)
            notes.Add($"Vendor {vid:X4} is an OEM identifier shared by many brands. Detect by interface " +
                      "signature rather than by USB id.");

        if (vid == 0x1532)
            notes.Add("Razer devices take lighting over control transfers to interface 0, not over a " +
                      "vendor HID collection.");

        // Which interfaces could carry a write at all.
        foreach (var d in usb)
        {
            var writable = d.Interfaces.Where(i => i.HasInterruptOut).Select(i => i.Number).Distinct().ToList();
            if (writable.Count > 0)
                notes.Add($"Interfaces with an interrupt OUT endpoint: {string.Join(", ", writable)}. " +
                          "Others need the control pipe.");
            else if (d.Interfaces.Count > 0)
                notes.Add("No interface has an interrupt OUT endpoint, so every write has to go down " +
                          "the control pipe.");
        }

        if (notes.Count == 0) return;

        sb.AppendLine("What this looks like");
        sb.AppendLine("--------------------");
        foreach (var n in notes.Distinct()) sb.AppendLine($"  - {n}");
        sb.AppendLine();
    }

    // ---------------------------------------------------------------- collection

    private static void AppendCollection(StringBuilder sb, HidNative.HidCollection c,
                                         List<UsbTopology.UsbDevice> usb)
    {
        int? iface = InterfaceNumber(c.Path);
        int? col = CollectionNumber(c.Path);

        sb.AppendLine("--------------------------------------------------------------------");
        sb.AppendLine($"COLLECTION  usage page 0x{c.UsagePage:X4} ({HidUsageNames.Page(c.UsagePage)})" +
                      $", usage 0x{c.Usage:X4}   {Describe(c)}");

        string where = iface is { } n
            ? $"USB interface {n}" + (col is { } k ? $", collection {k}" : "")
            : "interface not stated in the path";
        sb.AppendLine($"  location     : {where}");
        sb.AppendLine($"  reports      : input {c.InputReportLength}, output {c.OutputReportLength}, " +
                      $"feature {c.FeatureReportLength} bytes (report id included)");

        string vendorNote = HidUsageNames.VendorPageNote(c.UsagePage, c.VendorId);
        if (vendorNote.Length > 0) sb.AppendLine($"  note         : {vendorNote}");

        AppendTransport(sb, c, iface, usb);

        sb.AppendLine($"  path         : {c.Path}");

        using var handle = HidNative.CreateFile(c.Path, 0, HidNative.FILE_SHARE_READ_WRITE,
                                                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            sb.AppendLine("  (could not open for inspection)");
            sb.AppendLine();
            return;
        }

        if (HidD_GetNumInputBuffers(handle, out uint buffers))
            sb.AppendLine($"  input queue  : {buffers} reports");

        AppendIndexedStrings(sb, handle);

        var inputIds = new List<byte>();

        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                AppendLinkCollections(sb, preparsed);
                inputIds = AppendCaps(sb, preparsed);
                AppendDeclaredReports(sb, preparsed, c, inputIds);
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        else
        {
            sb.AppendLine("  (no preparsed data, so the report structure is unavailable)");
        }

        AppendInputReportProbe(sb, c, inputIds);

        if (c.FeatureReportLength > 0) AppendFeatureProbe(sb, c);

        AppendReportDescriptorBlob(sb, c);

        // Gated on the usage as well as the page: 0xFF60 alone is just a vendor page, and
        // sending VIA getters to something that is not QMK is not worth the risk.
        if (c.UsagePage == 0xFF60 && c.Usage == 0x61) AppendViaProbe(sb, c);

        sb.AppendLine();
    }

    /// <summary>
    /// Says how a driver would actually write to this collection.
    ///
    /// Three separate facts decide it, and conflating them is how the 1.4.1 bug happened:
    /// whether the collection declares output reports at all, whether it declares feature
    /// reports, and whether the USB interface underneath owns an interrupt OUT endpoint.
    /// A collection can declare output reports and still have no endpoint to carry them,
    /// in which case an ordinary write fails and everything has to go down the control pipe.
    /// </summary>
    private static void AppendTransport(StringBuilder sb, HidNative.HidCollection c, int? iface,
                                        List<UsbTopology.UsbDevice> usb)
    {
        var match = iface is { } number
            ? usb.SelectMany(d => d.Interfaces).FirstOrDefault(i => i.Number == number)
            : null;

        var routes = new List<string>();

        if (c.OutputReportLength > 0)
        {
            if (match is null)
                routes.Add($"output reports ({c.OutputReportLength} bytes) via HidD_SetOutputReport, " +
                           "and possibly WriteFile; the endpoint list was unavailable");
            else if (match.HasInterruptOut)
                routes.Add($"output reports ({c.OutputReportLength} bytes) via WriteFile on the " +
                           "interrupt OUT endpoint, or HidD_SetOutputReport");
            else
                routes.Add($"output reports ({c.OutputReportLength} bytes) via HidD_SetOutputReport " +
                           "ONLY. There is no interrupt OUT endpoint on this interface, so WriteFile " +
                           "will fail");
        }

        if (c.FeatureReportLength > 0)
            routes.Add($"feature reports ({c.FeatureReportLength} bytes) via HidD_SetFeature and " +
                       "HidD_GetFeature, which always use the control pipe");

        if (routes.Count == 0)
            routes.Add("nothing. This collection declares neither output nor feature reports, so it " +
                       "can only be read from");

        sb.AppendLine("  writable via : " + routes[0]);
        for (int i = 1; i < routes.Count; i++) sb.AppendLine("                 " + routes[i]);
    }

    /// <summary>
    /// Reads the string descriptors the device carries beyond the three standard ones.
    ///
    /// Vendors put firmware builds, model codes and internal names here, and a name like
    /// "eevision_A_kb_001" tells you which protocol family you are looking at outright.
    /// </summary>
    private static void AppendIndexedStrings(StringBuilder sb, SafeFileHandleEx handle)
    {
        var found = new List<string>();

        for (uint i = 1; i <= 8; i++)
        {
            var buffer = new byte[256];
            bool ok;
            try { ok = HidD_GetIndexedString(handle, i, buffer, buffer.Length); }
            catch { break; }
            if (!ok) continue;

            string text = Encoding.Unicode.GetString(buffer);
            int nul = text.IndexOf('\0');
            text = (nul >= 0 ? text[..nul] : text).Trim();

            if (text.Length > 0) found.Add($"{i}: \"{text}\"");
        }

        if (found.Count > 0) sb.AppendLine($"  strings      : {string.Join("  ", found)}");
    }

    /// <summary>
    /// Prints the collection tree.
    ///
    /// The tree is what tells you that a vendor blob is nested inside a keyboard collection
    /// rather than standing on its own, which changes how Windows treats it.
    /// </summary>
    private static void AppendLinkCollections(StringBuilder sb, IntPtr preparsed)
    {
        uint count = 0;
        HidP_GetLinkCollectionNodes(Array.Empty<HIDP_LINK_COLLECTION_NODE>(), ref count, preparsed);
        if (count == 0 || count > 512) return;

        var nodes = new HIDP_LINK_COLLECTION_NODE[count];
        if (HidP_GetLinkCollectionNodes(nodes, ref count, preparsed) != HIDP_STATUS_SUCCESS) return;
        if (count <= 1) return;   // a single application collection says nothing extra

        sb.AppendLine($"  collection tree ({count} nodes):");

        void Walk(int index, int depth)
        {
            if (index < 0 || index >= count || depth > 8) return;

            var n = nodes[index];
            string pad = new string(' ', 4 + depth * 2);
            string usageName = HidUsageNames.TopLevel(n.LinkUsagePage, n.LinkUsage);

            sb.AppendLine($"{pad}[{index}] {HidUsageNames.CollectionType(n.CollectionType)}" +
                          $"  page 0x{n.LinkUsagePage:X4}  usage 0x{n.LinkUsage:X4}" +
                          (usageName.Length > 0 ? $"  {usageName}" : "") +
                          (n.IsAlias ? "  (alias)" : ""));

            for (int child = n.FirstChild; child != 0; child = nodes[child].NextSibling)
            {
                Walk(child, depth + 1);
                if (child == nodes[child].NextSibling) break;   // malformed tree guard
            }
        }

        // Node 0 is the root of the top level collection.
        Walk(0, 0);
    }

    /// <summary>
    /// Dumps the parsed report structure, which is the closest thing Windows offers to the
    /// device's report descriptor and is what a driver has to match byte for byte.
    /// </summary>
    /// <summary>
    /// Finds every report id the descriptor declares, by asking hid.dll about all 256 of
    /// them for each direction.
    ///
    /// The caps arrays are not a complete list. A report made entirely of constant or
    /// padding items produces neither a button cap nor a value cap, so its id appears
    /// nowhere in them, and vendor blob reports are often declared exactly that way. This
    /// finds those. It runs inside hid.dll and sends nothing to the device.
    /// </summary>
    private static void AppendDeclaredReports(StringBuilder sb, IntPtr preparsed,
                                              HidNative.HidCollection c, List<byte> fromCaps)
    {
        var found = new List<(string Kind, byte Id, byte[] Template)>();

        foreach (var (type, name, length) in new[]
                 {
                     (HidP_Input,   "input",   c.InputReportLength),
                     (HidP_Output,  "output",  c.OutputReportLength),
                     (HidP_Feature, "feature", c.FeatureReportLength),
                 })
        {
            if (length <= 0) continue;

            for (int id = 0; id <= 0xFF; id++)
            {
                var report = new byte[length];
                int status;

                // The length has to be exactly the declared one or every call fails and it
                // looks as though the collection declares nothing at all.
                try { status = HidP_InitializeReportForID(type, (byte)id, preparsed, report, (uint)length); }
                catch { return; }

                if (status == HIDP_STATUS_SUCCESS) found.Add((name, (byte)id, report));
            }
        }

        if (found.Count == 0) return;

        sb.AppendLine("  report ids that exist (asked of every id, nothing sent to the device):");

        foreach (var kind in new[] { "input", "output", "feature" })
        {
            var ids = found.Where(f => f.Kind == kind).ToList();
            if (ids.Count == 0) continue;

            var labels = ids.Select(f =>
            {
                bool hidden = kind == "input" && !fromCaps.Contains(f.Id);
                return $"0x{f.Id:X2}" + (hidden ? "*" : "");
            });

            sb.AppendLine($"    {kind,-8}: {string.Join(", ", labels)}");
        }

        if (found.Any(f => f.Kind == "input" && !fromCaps.Contains(f.Id)))
            sb.AppendLine("    * declared but carrying no named fields, which usually means a vendor blob");

        // The template a report starts out as. Non-zero bytes here are defaults the
        // descriptor asked for rather than anything the device sent.
        foreach (var f in found.Where(f => f.Template.Skip(1).Any(b => b != 0)).Take(6))
            sb.AppendLine($"    {f.Kind} 0x{f.Id:X2} non-zero at rest: {Hex(f.Template, 32)}");
    }

    /// <summary>
    /// Dumps the parsed descriptor blob Windows keeps for the collection.
    ///
    /// Windows has no user mode call that returns a device's raw HID report descriptor, and
    /// the hub refuses to fetch one on an interface's behalf. This blob is the substitute
    /// and is in some ways better: it carries the byte and bit position of every field,
    /// along with the constant and padding items the caps arrays drop entirely.
    ///
    /// It is emitted as base64 rather than decoded here. The layout is undocumented and
    /// changes between Windows versions, so decoding it in this app would be a standing
    /// liability, while the bytes themselves can be turned back into a compilable report
    /// descriptor offline by tooling that already exists for the job.
    /// </summary>
    private static void AppendReportDescriptorBlob(StringBuilder sb, HidNative.HidCollection c)
    {
        using var handle = HidNative.CreateFile(c.Path, 0, HidNative.FILE_SHARE_READ_WRITE,
                                                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return;

        // HID_COLLECTION_INFORMATION: DescriptorSize, Polled, Reserved1, VID, PID, Version.
        var info = new byte[12];
        int returned = 0;

        bool ok;
        try
        {
            ok = DeviceIoControl(handle, IoctlHidGetCollectionInformation, IntPtr.Zero, 0,
                                 info, info.Length, ref returned, IntPtr.Zero);
        }
        catch { return; }

        if (!ok || returned < 4) return;

        int size = BitConverter.ToInt32(info, 0);
        if (size <= 0 || size > 64 * 1024) return;

        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return;

        try
        {
            var blob = new byte[size];
            Marshal.Copy(preparsed, blob, 0, size);

            // "HidP KDR" marks the layout this was captured from. Printing it means a
            // future Windows change shows up as a different magic rather than as silently
            // wrong output somewhere downstream.
            string magic = Encoding.ASCII.GetString(blob, 0, Math.Min(8, blob.Length));
            bool known = magic == "HidP KDR";

            sb.AppendLine($"  parsed descriptor blob: {size} bytes, magic \"{magic}\"" +
                          (known ? "" : "  (unrecognised layout)"));
            sb.AppendLine("    Base64. This can be turned back into a report descriptor offline,");
            sb.AppendLine("    which is the nearest thing to reading it off the device itself.");

            string base64 = Convert.ToBase64String(blob);
            for (int i = 0; i < base64.Length; i += 76)
                sb.AppendLine("    " + base64.Substring(i, Math.Min(76, base64.Length - i)));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  parsed descriptor blob: could not be read ({ex.Message})");
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    /// <summary>Returns the input report ids found, so they can be read back afterwards.</summary>
    private static List<byte> AppendCaps(StringBuilder sb, IntPtr preparsed)
    {
        var inputIds = new List<byte>();

        var caps = new HidNative.HIDP_CAPS();
        if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return inputIds;

        // Collected so the report ids can be summarised before the detail.
        var inventory = new SortedDictionary<(string Kind, byte Id), int>();

        foreach (var (type, name, valueCount, buttonCount) in new[]
                 {
                     (HidP_Input,   "input",   caps.NumberInputValueCaps,   caps.NumberInputButtonCaps),
                     (HidP_Output,  "output",  caps.NumberOutputValueCaps,  caps.NumberOutputButtonCaps),
                     (HidP_Feature, "feature", caps.NumberFeatureValueCaps, caps.NumberFeatureButtonCaps),
                 })
        {
            if (valueCount == 0 && buttonCount == 0) continue;

            sb.AppendLine($"  {name} fields:");

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
                            ? $"usages 0x{b.UsageMin:X4}-0x{b.UsageMax:X4}"
                            : $"usage 0x{b.Usage:X4}";

                        int span = b.IsRange != 0 ? b.UsageMax - b.UsageMin + 1 : 1;
                        Add(inventory, name, b.ReportID, span);
                        if (type == HidP_Input) inputIds.Add(b.ReportID);

                        sb.AppendLine($"    buttons  report 0x{b.ReportID:X2}  page 0x{b.UsagePage:X4} " +
                                      $"({HidUsageNames.Page(b.UsagePage)})  {usage}");
                        sb.AppendLine($"             collection {b.LinkCollection}  " +
                                      $"[{HidUsageNames.BitField(b.BitField)}]");
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
                            ? $"usages 0x{v.UsageMin:X4}-0x{v.UsageMax:X4}"
                            : $"usage 0x{v.Usage:X4}";

                        Add(inventory, name, v.ReportID, v.ReportCount * v.BitSize);
                        if (type == HidP_Input) inputIds.Add(v.ReportID);

                        sb.AppendLine($"    values   report 0x{v.ReportID:X2}  page 0x{v.UsagePage:X4} " +
                                      $"({HidUsageNames.Page(v.UsagePage)})  {usage}");
                        sb.AppendLine($"             {v.ReportCount} x {v.BitSize} bits = " +
                                      $"{v.ReportCount * v.BitSize / 8.0:0.#} bytes  " +
                                      $"logical {v.LogicalMin}..{v.LogicalMax}" +
                                      (v.PhysicalMin != 0 || v.PhysicalMax != 0
                                          ? $"  physical {v.PhysicalMin}..{v.PhysicalMax}" : ""));
                        sb.AppendLine($"             collection {v.LinkCollection}  " +
                                      $"[{HidUsageNames.BitField(v.BitField)}]" +
                                      (v.HasNull != 0 ? "  hasNull" : "") +
                                      (v.Units != 0 ? $"  units 0x{v.Units:X}/exp {v.UnitsExp}" : ""));
                    }
                }
            }
        }

        if (inventory.Count > 0)
        {
            sb.AppendLine("  report ids declared:");
            foreach (var kind in new[] { "input", "output", "feature" })
            {
                var ids = inventory.Where(kv => kv.Key.Kind == kind).ToList();
                if (ids.Count == 0) continue;

                sb.AppendLine($"    {kind,-8}: " + string.Join(", ",
                    ids.Select(kv => $"0x{kv.Key.Id:X2} ({kv.Value / 8.0:0.#} bytes of fields)")));
            }
        }

        return inputIds;

        static void Add(SortedDictionary<(string, byte), int> map, string kind, byte id, int bits)
        {
            map.TryGetValue((kind, id), out int existing);
            map[(kind, id)] = existing + bits;
        }
    }

    /// <summary>
    /// Reads every feature report the device declares, then sweeps the rest.
    ///
    /// Purely a read. A feature report is where firmware versions, capability bitmaps and
    /// stored lighting state live, and reading them is how several of the protocols this
    /// app already speaks were worked out.
    ///
    /// Failures are printed rather than hidden, and so are all-zero answers: "this id exists
    /// and reads back as zeroes" is a different fact from "this id does not answer", and a
    /// driver author needs to tell them apart.
    /// </summary>
    private static void AppendFeatureProbe(StringBuilder sb, HidNative.HidCollection c)
    {
        sb.AppendLine("  feature reports (read only):");

        using var handle = HidNative.CreateFile(c.Path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            sb.AppendLine("    could not open for reading: held by another process, or reserved by Windows");
            return;
        }

        // The buffer has to be exactly the declared length or Windows rejects the call.
        int length = c.FeatureReportLength;
        var failures = new SortedDictionary<int, int>();

        // Answers are grouped by content. Firmware very often returns the same block for a
        // run of ids, and printing it once with the list of ids that produced it is both
        // shorter and more informative than fifteen near-identical lines.
        var answers = new List<(byte Id, byte[] Data)>();

        for (int id = 0x00; id <= 0xFF; id++)
        {
            var buffer = new byte[length];
            buffer[0] = (byte)id;

            bool ok;
            int error = 0;
            try
            {
                ok = HidNative.HidD_GetFeature(handle, buffer, buffer.Length);
                if (!ok) error = Marshal.GetLastWin32Error();
            }
            catch { continue; }

            if (!ok)
            {
                failures.TryGetValue(error, out int n);
                failures[error] = n + 1;
                continue;
            }

            answers.Add(((byte)id, buffer));

            // A collection that answers everything is not selecting on the id at all.
            if (answers.Count > 64)
            {
                sb.AppendLine("    (stopping the sweep: this collection answers nearly every id, so it");
                sb.AppendLine("     is not selecting on the report id)");
                break;
            }
        }

        if (answers.Count == 0)
        {
            sb.AppendLine("    no report id answered");
            if (failures.Count > 0)
                sb.AppendLine($"    errors seen: {string.Join(", ", failures.Select(f => $"{ErrorName(f.Key)} x{f.Value}"))}");
            return;
        }

        sb.AppendLine($"    declared length {length} bytes, {answers.Count} id(s) answered");

        // Group on everything after the leading id byte, since that byte is the request.
        var groups = answers
            .GroupBy(a => Convert.ToHexString(a.Data, 1, a.Data.Length - 1))
            .ToList();

        foreach (var group in groups)
        {
            var ids = group.Select(g => $"0x{g.Id:X2}").ToList();
            var data = group.First().Data;
            bool allZero = data.Skip(1).All(b => b == 0);

            string heading = ids.Count == 1 ? ids[0] : $"{ids.Count} ids: {string.Join(" ", ids)}";
            sb.AppendLine($"    {heading}{(allZero ? "   (all zero)" : "")}");

            if (!allZero)
            {
                // Trailing zeroes carry nothing, so the dump stops where the data does.
                int last = data.Length - 1;
                while (last > 0 && data[last] == 0) last--;
                int show = Math.Min(data.Length, Math.Max(32, last + 1));

                AppendHexBlock(sb, data[..show], "      ");
                if (show < data.Length)
                    sb.AppendLine($"      ... {data.Length - show} further bytes, all zero");
            }
        }

        if (groups.Count == 1 && answers.Count > 1)
            sb.AppendLine("    every answering id returned the same block, so the id is being ignored");
    }

    /// <summary>
    /// Asks for the current contents of each declared input report.
    ///
    /// This is GET_REPORT over the control pipe, which is a standard read and distinct from
    /// waiting for the device to send something. It shows the state a device is holding
    /// right now without anyone having to touch the hardware, which is exactly what is
    /// missing when the device is on someone else's desk.
    /// </summary>
    private static void AppendInputReportProbe(StringBuilder sb, HidNative.HidCollection c, IEnumerable<byte> ids)
    {
        var wanted = ids.Distinct().OrderBy(x => x).ToList();
        if (wanted.Count == 0 || c.InputReportLength <= 0) return;

        using var handle = HidNative.CreateFile(c.Path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE, HidNative.FILE_SHARE_READ_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) return;

        var lines = new List<string>();

        foreach (byte id in wanted)
        {
            var buffer = new byte[c.InputReportLength];
            buffer[0] = id;

            bool ok;
            try { ok = HidD_GetInputReport(handle, buffer, buffer.Length); }
            catch { continue; }

            if (ok) lines.Add($"    0x{id:X2}: {Hex(buffer, 40)}");
        }

        if (lines.Count == 0) return;

        sb.AppendLine("  current input report contents (read only):");
        foreach (var line in lines) sb.AppendLine(line);
    }

    private static string ErrorName(int code) => code switch
    {
        0 => "no error reported",
        1 => "ERROR_INVALID_FUNCTION",
        6 => "ERROR_INVALID_HANDLE",
        22 => "ERROR_BAD_COMMAND",
        31 => "ERROR_GEN_FAILURE",
        87 => "ERROR_INVALID_PARAMETER",
        995 => "ERROR_OPERATION_ABORTED",
        1167 => "ERROR_DEVICE_NOT_CONNECTED",
        _ => $"error {code}",
    };

    /// <summary>
    /// Asks a QMK raw HID endpoint what it supports.
    ///
    /// The only part of this file that sends anything. VIA is request and response, so a
    /// getter has to go out before anything comes back. Both packets used here are getters:
    /// one asks for the protocol version, the other reads a stored brightness. Neither sets
    /// anything.
    /// </summary>
    private static void AppendViaProbe(StringBuilder sb, HidNative.HidCollection c)
    {
        sb.AppendLine("  VIA probe (sends two getter packets, sets nothing):");

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

    // ---------------------------------------------------------------- helpers

    /// <summary>Pulls the USB interface number out of a device interface path (mi_01).</summary>
    public static int? InterfaceNumber(string path)
    {
        int at = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        return int.TryParse(path.AsSpan(at + 4, 2), System.Globalization.NumberStyles.HexNumber,
                            null, out int value) ? value : null;
    }

    /// <summary>Pulls the collection index out of a device interface path (col04).</summary>
    public static int? CollectionNumber(string path)
    {
        int at = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        return int.TryParse(path.AsSpan(at + 4, 2), System.Globalization.NumberStyles.HexNumber,
                            null, out int value) ? value : null;
    }

    public static string Hex(byte[] data, int max)
    {
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        if (data.Length > n) sb.Append("...");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Sixteen bytes a line with offsets, because a long descriptor is read by counting
    /// into it and an unbroken run of hex makes that needlessly hard.
    /// </summary>
    private static void AppendHexBlock(StringBuilder sb, byte[] data, string indent)
    {
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int n = Math.Min(16, data.Length - offset);
            var line = new StringBuilder();
            line.Append(indent).Append($"{offset:X4}  ");
            for (int i = 0; i < n; i++) line.Append(data[offset + i].ToString("X2")).Append(' ');
            sb.AppendLine(line.ToString().TrimEnd());
        }
    }
}
