using System.Runtime.InteropServices;

namespace SickRGB.Hardware;

/// <summary>
/// Minimal Win32 HID interop. Only what we need to find, open and write to the
/// keyboard's vendor-defined collections.
/// </summary>
internal static class HidNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ_WRITE = 0x00000003;
    public const uint OPEN_EXISTING = 3;

    private const int DIGCF_PRESENT = 0x00000002;
    private const int DIGCF_DEVICEINTERFACE = 0x00000010;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandleEx h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandleEx h, out IntPtr preparsed);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandleEx h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandleEx h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetOutputReport(SafeFileHandleEx h, byte[] buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandleEx h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, ref int required, IntPtr devInfoData);

    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandleEx CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandleEx h, byte[] buf, int len, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandleEx h, byte[] buf, int len, out int read, IntPtr overlapped);

    /// <summary>Unblocks a pending ReadFile so a HID query can time out instead of hanging forever.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandleEx h, IntPtr overlapped);

    /// <summary>
    /// Reads one report, giving up after <paramref name="timeoutMs"/>.
    ///
    /// HID reads block until the device has something to say, which for a device that
    /// does not understand the request is forever. The pending read is cancelled on
    /// timeout so the handle stays usable for the next attempt.
    /// </summary>
    public static byte[]? ReadWithTimeout(SafeFileHandleEx handle, int length, int timeoutMs)
    {
        var buffer = new byte[length];
        var read = Task.Run(() => ReadFile(handle, buffer, buffer.Length, out _, IntPtr.Zero));

        if (!read.Wait(timeoutMs))
        {
            try { CancelIoEx(handle, IntPtr.Zero); } catch { /* handle already closing */ }
            return null;
        }

        return read.Result ? buffer : null;
    }

    /// <summary>One HID collection (a device can expose several).</summary>
    public sealed record HidCollection(
        string Path,
        ushort VendorId,
        ushort ProductId,
        ushort VersionNumber,
        ushort UsagePage,
        ushort Usage,
        int InputReportLength,
        int OutputReportLength,
        int FeatureReportLength,
        string Product);

    public static List<HidCollection> Enumerate()
    {
        var results = new List<HidCollection>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return results;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
            {
                int required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, required, ref required, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;

                    // Open with zero access: succeeds even when another process holds the device.
                    using var probe = CreateFile(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (probe.IsInvalid) continue;

                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(probe, ref attr)) continue;

                    ushort usagePage = 0, usage = 0;
                    int inLen = 0, outLen = 0, featLen = 0;

                    if (HidD_GetPreparsedData(probe, out IntPtr pp))
                    {
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pp, ref caps) == HIDP_STATUS_SUCCESS)
                            {
                                usagePage = caps.UsagePage;
                                usage = caps.Usage;
                                inLen = caps.InputReportByteLength;
                                outLen = caps.OutputReportByteLength;
                                featLen = caps.FeatureReportByteLength;
                            }
                        }
                        finally { HidD_FreePreparsedData(pp); }
                    }

                    string product = "";
                    var nameBuf = new byte[512];
                    if (HidD_GetProductString(probe, nameBuf, nameBuf.Length))
                    {
                        product = System.Text.Encoding.Unicode.GetString(nameBuf);
                        int nul = product.IndexOf('\0');
                        if (nul >= 0) product = product[..nul];
                    }

                    results.Add(new HidCollection(path, attr.VendorID, attr.ProductID, attr.VersionNumber,
                                                  usagePage, usage, inLen, outLen, featLen, product));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return results;
    }
}

/// <summary>SafeHandle wrapper so device handles are always released.</summary>
internal sealed class SafeFileHandleEx : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFileHandleEx() : base(true) { }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    protected override bool ReleaseHandle() => CloseHandle(handle);
}
