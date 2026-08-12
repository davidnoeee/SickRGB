using System.Runtime.InteropServices;
using System.Text;

namespace SickRGB.Hardware;

/// <summary>
/// Reads USB descriptors straight from the hub the device is plugged into.
///
/// Windows' HID API describes what a device's reports look like but says nothing about the
/// USB plumbing underneath. That gap is expensive: writing a driver blind means guessing
/// whether an interface even has an endpoint to write to. The 1.4.1 fix for vendor
/// keyboards came down to exactly that question, and it had to be inferred from the shape
/// of the collections rather than read off.
///
/// Asking the hub closes the gap. It hands back the device descriptor, the complete
/// configuration descriptor with every interface and every endpoint, and the string
/// descriptors. From those you can tell at a glance whether a lighting interface can be
/// written to with an ordinary write, or whether everything has to go down the control pipe.
///
/// This needs no administrator rights: hubs open for read and write from a normal account.
///
/// The one thing the hub will not hand over is the HID report descriptor itself. Requests
/// aimed at an interface rather than the device come back as a general failure, which is a
/// documented limitation of the hub driver rather than a permissions problem. The parsed
/// report structure from hid.dll covers that ground instead.
///
/// Everything here is a read. Nothing is sent to a device that changes its state.
/// </summary>
internal static class UsbTopology
{
    // ---------------------------------------------------------------- interop

    private static readonly Guid GuidDevInterfaceUsbHub =
        new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    private const uint IoctlGetNodeConnectionInformationEx = 0x220448;
    private const uint IoctlGetDescriptorFromNodeConnection = 0x220410;

    // Standard USB descriptor types.
    private const byte DescriptorDevice = 0x01;
    private const byte DescriptorConfiguration = 0x02;
    private const byte DescriptorString = 0x03;

    /// <summary>Header of USB_DESCRIPTOR_REQUEST: port index, then an 8 byte setup packet.</summary>
    private const int DescriptorRequestHeader = 12;

    /// <summary>
    /// USB_NODE_CONNECTION_INFORMATION_EX is a 35 byte head followed by one 11 byte pipe
    /// entry per open endpoint. Both numbers are byte exact rather than aligned, which was
    /// confirmed against real hardware: the returned length always came back as
    /// 35 + 11 * NumberOfOpenPipes.
    ///
    /// Sized for more pipes than any real device has, because the call fails outright if
    /// the buffer cannot hold the whole tail.
    /// </summary>
    private const int NodeConnectionHeadSize = 35;
    private const int NodeConnectionPipeSize = 11;
    private const int NodeConnectionInfoSize = NodeConnectionHeadSize + 30 * NodeConnectionPipeSize;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
                                                      IntPtr parent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid,
                                                           int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data,
                                                                IntPtr detail, int size, ref int required,
                                                                IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandleEx handle, uint code,
                                               byte[] input, int inputSize,
                                               byte[] output, int outputSize,
                                               ref int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;

    // ---------------------------------------------------------------- model

    /// <summary>One endpoint on one interface.</summary>
    internal sealed record UsbEndpoint(byte Address, byte Attributes, ushort MaxPacketSize, byte Interval)
    {
        public bool IsIn => (Address & 0x80) != 0;
        public int Number => Address & 0x0F;

        public string TransferType => (Attributes & 0x03) switch
        {
            0 => "control",
            1 => "isochronous",
            2 => "bulk",
            _ => "interrupt",
        };

        public override string ToString() =>
            $"0x{Address:X2}  {(IsIn ? "IN " : "OUT")}  {TransferType,-11}  {MaxPacketSize,4} bytes  interval {Interval}";
    }

    /// <summary>A HID class descriptor, which carries the length of the report descriptor.</summary>
    internal sealed record UsbHidDescriptor(ushort BcdHid, byte CountryCode, byte DescriptorType, ushort DescriptorLength);

    internal sealed record UsbInterface(
        byte Number,
        byte AlternateSetting,
        byte Class,
        byte SubClass,
        byte Protocol,
        byte StringIndex,
        List<UsbEndpoint> Endpoints,
        UsbHidDescriptor? Hid)
    {
        public bool HasInterruptOut => Endpoints.Any(e => !e.IsIn && (e.Attributes & 0x03) == 3);

        public string ClassName => Class switch
        {
            0x01 => "audio",
            0x03 => "HID",
            0x08 => "mass storage",
            0x09 => "hub",
            0x0B => "smart card",
            0x0E => "video",
            0xFF => "vendor specific",
            _ => $"class 0x{Class:X2}",
        };

        /// <summary>Boot protocol tells you the interface is the one Windows claims for typing.</summary>
        public string HidProtocolName => Class == 0x03
            ? SubClass == 0x01
                ? Protocol switch { 1 => "boot keyboard", 2 => "boot mouse", _ => "boot" }
                : "no boot protocol"
            : "";
    }

    internal sealed record UsbDevice(
        string HubPath,
        int Port,
        ushort VendorId,
        ushort ProductId,
        ushort BcdDevice,
        ushort BcdUsb,
        byte DeviceClass,
        byte DeviceSubClass,
        byte DeviceProtocol,
        byte MaxPacketSize0,
        byte NumConfigurations,
        int Speed,
        ushort Address,
        uint OpenPipes,
        uint ConnectionStatus,
        string Manufacturer,
        string Product,
        string Serial,
        List<UsbInterface> Interfaces,
        byte[]? RawDeviceDescriptor,
        byte[]? RawConfigDescriptor,
        string? Error)
    {
        public string SpeedName => Speed switch
        {
            0 => "low (1.5 Mbit)",
            1 => "full (12 Mbit)",
            2 => "high (480 Mbit)",
            3 => "super (5 Gbit)",
            _ => $"speed code {Speed}",
        };

        public string StatusName => ConnectionStatus switch
        {
            0 => "no device",
            1 => "connected",
            2 => "failed enumeration",
            3 => "general failure",
            4 => "caused overcurrent",
            5 => "not enough power",
            _ => $"status {ConnectionStatus}",
        };
    }

    // ---------------------------------------------------------------- enumeration

    /// <summary>
    /// Walks every hub and returns every device plugged into one.
    ///
    /// Devices are matched to HID collections by vendor and product id afterwards. That is
    /// ambiguous only when two identical devices are plugged in at once, and the report
    /// says so rather than picking one.
    /// </summary>
    public static List<UsbDevice> Enumerate(ushort? vendorFilter = null)
    {
        var devices = new List<UsbDevice>();
        var guid = GuidDevInterfaceUsbHub;

        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                                          DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return devices;

        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string? hubPath = InterfacePath(set, ref data);
                if (hubPath is null) continue;

                try { ReadHub(hubPath, vendorFilter, devices); }
                catch { /* one unreadable hub must not lose the rest */ }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return devices;
    }

    private static string? InterfacePath(IntPtr set, ref SpDeviceInterfaceData data)
    {
        int required = 0;
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required <= 0) return null;

        IntPtr detail = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, ref required, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    private static void ReadHub(string hubPath, ushort? vendorFilter, List<UsbDevice> devices)
    {
        // Opened with no access at all. Every hub IOCTL used here is FILE_ANY_ACCESS, so
        // asking for read and write buys nothing and would be the first thing to fail on a
        // locked down machine.
        using var hub = HidNative.CreateFile(hubPath, 0, HidNative.FILE_SHARE_READ_WRITE,
                                             IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (hub.IsInvalid) return;

        // Ports are asked one at a time. There is no count to read first that is cheaper
        // than simply asking, and an empty port answers with a zero vendor id.
        for (int port = 1; port <= 32; port++)
        {
            var buffer = new byte[NodeConnectionInfoSize];
            BitConverter.GetBytes(port).CopyTo(buffer, 0);

            int returned = 0;
            if (!DeviceIoControl(hub, IoctlGetNodeConnectionInformationEx,
                                 buffer, buffer.Length, buffer, buffer.Length, ref returned, IntPtr.Zero))
                continue;

            // USB_NODE_CONNECTION_INFORMATION_EX, byte packed with no alignment padding:
            //   0  ConnectionIndex (4)
            //   4  DeviceDescriptor (18)
            //   22 CurrentConfigurationValue (1)
            //   23 Speed (1)
            //   24 DeviceIsHub (1)
            //   25 DeviceAddress (2)
            //   27 NumberOfOpenPipes (4)
            //   31 ConnectionStatus (4)
            //   35 PipeList, 11 bytes each
            //
            // The packing was confirmed against real hardware rather than assumed: a
            // keyboard with five endpoints reports five open pipes and returns exactly
            // 35 + 5 * 11 bytes, and the same arithmetic holds for a two endpoint device.
            // Reading DeviceAddress at an aligned offset 26 instead yields nonsense.
            var descriptor = buffer[4..22];
            ushort vid = BitConverter.ToUInt16(descriptor, 8);
            ushort pid = BitConverter.ToUInt16(descriptor, 10);

            if (vid == 0) continue;
            if (vendorFilter is { } want && vid != want) continue;

            byte speed = buffer[23];
            ushort address = BitConverter.ToUInt16(buffer, 25);
            uint openPipes = BitConverter.ToUInt32(buffer, 27);
            uint status = BitConverter.ToUInt32(buffer, 31);

            devices.Add(ReadDevice(hub, hubPath, port, descriptor, speed, address, openPipes, status));
        }
    }

    private static UsbDevice ReadDevice(SafeFileHandleEx hub, string hubPath, int port,
                                        byte[] deviceDescriptor, byte speed, ushort address,
                                        uint openPipes, uint status)
    {
        ushort bcdUsb = BitConverter.ToUInt16(deviceDescriptor, 2);
        byte deviceClass = deviceDescriptor[4];
        byte deviceSubClass = deviceDescriptor[5];
        byte deviceProtocol = deviceDescriptor[6];
        byte maxPacket0 = deviceDescriptor[7];
        ushort vid = BitConverter.ToUInt16(deviceDescriptor, 8);
        ushort pid = BitConverter.ToUInt16(deviceDescriptor, 10);
        ushort bcdDevice = BitConverter.ToUInt16(deviceDescriptor, 12);
        byte iManufacturer = deviceDescriptor[14];
        byte iProduct = deviceDescriptor[15];
        byte iSerial = deviceDescriptor[16];
        byte numConfigs = deviceDescriptor[17];

        string manufacturer = ReadString(hub, port, iManufacturer);
        string product = ReadString(hub, port, iProduct);
        string serial = ReadString(hub, port, iSerial);

        var interfaces = new List<UsbInterface>();
        byte[]? rawConfig = null;
        string? error = null;

        try
        {
            // The first nine bytes carry the total length, so the real read is sized exactly.
            var head = GetDescriptor(hub, port, DescriptorConfiguration, 0, 0, 9, 0x80);
            if (head is { Length: >= 4 })
            {
                int total = BitConverter.ToUInt16(head, 2);
                if (total is > 9 and <= 4096)
                {
                    rawConfig = GetDescriptor(hub, port, DescriptorConfiguration, 0, 0, total, 0x80);
                    if (rawConfig is not null) interfaces = ParseConfiguration(rawConfig);
                }
            }
            else
            {
                error = "the hub would not return a configuration descriptor";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new UsbDevice(hubPath, port, vid, pid, bcdDevice, bcdUsb, deviceClass, deviceSubClass,
                             deviceProtocol, maxPacket0, numConfigs, speed, address, openPipes, status,
                             manufacturer, product, serial, interfaces, deviceDescriptor, rawConfig, error);
    }

    /// <summary>
    /// Walks the configuration descriptor, which is a flat run of variable length records.
    /// Interfaces, their HID descriptors and their endpoints all arrive in one blob and
    /// belong to whichever interface most recently appeared.
    /// </summary>
    private static List<UsbInterface> ParseConfiguration(byte[] config)
    {
        var interfaces = new List<UsbInterface>();

        byte number = 0, alt = 0, cls = 0, sub = 0, proto = 0, stringIndex = 0;
        List<UsbEndpoint>? endpoints = null;
        UsbHidDescriptor? hid = null;
        bool open = false;

        void Flush()
        {
            if (!open) return;
            interfaces.Add(new UsbInterface(number, alt, cls, sub, proto, stringIndex,
                                            endpoints ?? new List<UsbEndpoint>(), hid));
            endpoints = null;
            hid = null;
            open = false;
        }

        int i = 0;
        while (i + 1 < config.Length)
        {
            int length = config[i];
            byte type = config[i + 1];

            // A zero length would spin forever, and a record running past the end is junk.
            if (length < 2 || i + length > config.Length) break;

            switch (type)
            {
                case 0x04 when length >= 9:      // interface
                    Flush();
                    number = config[i + 2];
                    alt = config[i + 3];
                    cls = config[i + 5];
                    sub = config[i + 6];
                    proto = config[i + 7];
                    stringIndex = config[i + 8];
                    endpoints = new List<UsbEndpoint>();
                    open = true;
                    break;

                case 0x05 when length >= 7:      // endpoint
                    endpoints?.Add(new UsbEndpoint(
                        config[i + 2],
                        config[i + 3],
                        BitConverter.ToUInt16(config, i + 4),
                        config[i + 6]));
                    break;

                case 0x21 when length >= 9:      // HID class descriptor
                    hid = new UsbHidDescriptor(
                        BitConverter.ToUInt16(config, i + 2),
                        config[i + 4],
                        config[i + 6],
                        BitConverter.ToUInt16(config, i + 7));
                    break;
            }

            i += length;
        }

        Flush();
        return interfaces;
    }

    private static string ReadString(SafeFileHandleEx hub, int port, byte index)
    {
        if (index == 0) return "";

        try
        {
            // 0x0409 is US English. A device that ships only one language answers to it
            // regardless, and one that does not simply returns nothing.
            var data = GetDescriptor(hub, port, DescriptorString, index, 0x0409, 255, 0x80);
            if (data is null || data.Length < 4) return "";

            // bLength, bDescriptorType, then UTF-16.
            int length = Math.Min(data[0], data.Length);
            if (length <= 2) return "";

            return Encoding.Unicode.GetString(data, 2, length - 2).TrimEnd('\0').Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Issues one GET_DESCRIPTOR through the hub on behalf of the device on a port.
    ///
    /// <paramref name="bmRequest"/> is 0x80 for descriptors belonging to the device. An
    /// interface recipient (0x81), which is what a HID report descriptor needs, is refused
    /// by the hub driver; the call is left possible because saying so in the report is more
    /// useful than pretending the option does not exist.
    /// </summary>
    private static byte[]? GetDescriptor(SafeFileHandleEx hub, int port, byte type, byte index,
                                         ushort languageOrInterface, int length, byte bmRequest)
    {
        var buffer = new byte[DescriptorRequestHeader + length];

        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        buffer[4] = bmRequest;
        buffer[5] = 0x06;                                              // GET_DESCRIPTOR
        BitConverter.GetBytes((ushort)((type << 8) | index)).CopyTo(buffer, 6);
        BitConverter.GetBytes(languageOrInterface).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)length).CopyTo(buffer, 10);

        int returned = 0;
        if (!DeviceIoControl(hub, IoctlGetDescriptorFromNodeConnection,
                             buffer, buffer.Length, buffer, buffer.Length, ref returned, IntPtr.Zero))
            return null;

        if (returned <= DescriptorRequestHeader) return null;
        return buffer[DescriptorRequestHeader..returned];
    }
}
