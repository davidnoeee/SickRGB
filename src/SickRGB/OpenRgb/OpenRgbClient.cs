using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using SickRGB.Core;

namespace SickRGB.OpenRgb;

/// <summary>OpenRGB's device categories, used to guess a sensible role and canvas shape.</summary>
public enum OpenRgbDeviceType
{
    Motherboard = 0, Dram = 1, Gpu = 2, Cooler = 3, LedStrip = 4, Keyboard = 5,
    Mouse = 6, Mousemat = 7, Headset = 8, HeadsetStand = 9, Gamepad = 10, Light = 11,
    Speaker = 12, Virtual = 13, Storage = 14, Case = 15, Microphone = 16,
    Accessory = 17, Keypad = 18, Unknown = 19,
}

public sealed class OpenRgbZone
{
    public int Index;
    public string Name = "";
    public int Type;
    public uint LedsCount;
    public uint LedsMin;
    public uint LedsMax;

    /// <summary>
    /// True for addressable headers, where the controller cannot tell how many LEDs are
    /// attached. These sit at 0 until someone says how long the strip is.
    /// </summary>
    public bool IsResizable => LedsMax > LedsMin;
}

public sealed class OpenRgbDevice
{
    public int Index;
    public OpenRgbDeviceType Type;
    public string Name = "";
    public string Vendor = "";
    public string Description = "";
    public string Version = "";
    public string Serial = "";
    public string Location = "";
    public List<OpenRgbZone> Zones = new();
    public List<string> LedNames = new();
    public int LedCount => LedNames.Count;
}

/// <summary>
/// Minimal client for the OpenRGB SDK network protocol.
///
/// We advertise protocol version 4 deliberately. The server negotiates down to
/// min(client, server), so this keeps us on a stable wire format and avoids the
/// protocol 5/6 additions we do not need.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private const uint ClientProtocolVersion = 4;

    private static readonly byte[] Magic = "ORGB"u8.ToArray();

    // Packet ids we use.
    private const uint PktRequestControllerCount = 0;
    private const uint PktRequestControllerData = 1;
    private const uint PktRequestProtocolVersion = 40;
    private const uint PktSetClientName = 50;
    private const uint PktResizeZone = 1000;
    private const uint PktUpdateLeds = 1050;
    private const uint PktSetCustomMode = 1100;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly object _io = new();

    public uint ProtocolVersion { get; private set; }
    public bool IsConnected => _tcp?.Connected == true;

    public bool Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 700)
    {
        Disconnect();
        try
        {
            var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
            {
                tcp.Dispose();
                return false;
            }

            tcp.NoDelay = true;
            _tcp = tcp;
            _stream = tcp.GetStream();
            _stream.ReadTimeout = 3000;
            _stream.WriteTimeout = 3000;

            NegotiateVersion();
            SendClientName("SickRGB");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenRGB] connect failed: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_io)
        {
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _stream = null;
            _tcp = null;
            ProtocolVersion = 0;
        }
    }

    // ------------------------------------------------------------------ wire helpers

    private void SendPacket(uint deviceId, uint packetId, byte[]? payload)
    {
        if (_stream is null) throw new IOException("not connected");
        payload ??= Array.Empty<byte>();

        var header = new byte[16];
        Buffer.BlockCopy(Magic, 0, header, 0, 4);
        BitConverter.TryWriteBytes(header.AsSpan(4), deviceId);
        BitConverter.TryWriteBytes(header.AsSpan(8), packetId);
        BitConverter.TryWriteBytes(header.AsSpan(12), (uint)payload.Length);

        _stream.Write(header, 0, header.Length);
        if (payload.Length > 0) _stream.Write(payload, 0, payload.Length);
        _stream.Flush();
    }

    /// <summary>Reads one packet, skipping any that are not the id we asked for.</summary>
    private byte[]? ReceivePacket(uint expectedPacketId, int maxSkips = 8)
    {
        if (_stream is null) return null;

        for (int i = 0; i < maxSkips; i++)
        {
            var header = ReadExactly(16);
            if (header is null) return null;

            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                return null;   // stream desynchronised; caller will reconnect

            uint packetId = BitConverter.ToUInt32(header, 8);
            uint size = BitConverter.ToUInt32(header, 12);

            // Guard against a bogus length turning into a huge allocation.
            if (size > 32 * 1024 * 1024) return null;

            var payload = size > 0 ? ReadExactly((int)size) : Array.Empty<byte>();
            if (payload is null) return null;

            if (packetId == expectedPacketId) return payload;
            // Anything else (e.g. DEVICE_LIST_UPDATED) is informational; keep looking.
        }
        return null;
    }

    private byte[]? ReadExactly(int count)
    {
        if (_stream is null) return null;
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read;
            try { read = _stream.Read(buffer, offset, count - offset); }
            catch { return null; }
            if (read <= 0) return null;
            offset += read;
        }
        return buffer;
    }

    private void NegotiateVersion()
    {
        lock (_io)
        {
            SendPacket(0, PktRequestProtocolVersion, BitConverter.GetBytes(ClientProtocolVersion));
            var reply = ReceivePacket(PktRequestProtocolVersion);

            // A protocol-0 server sends nothing back at all.
            uint server = reply is { Length: >= 4 } ? BitConverter.ToUInt32(reply, 0) : 0;
            ProtocolVersion = Math.Min(ClientProtocolVersion, server);
        }
    }

    private void SendClientName(string name)
    {
        lock (_io)
        {
            var bytes = Encoding.ASCII.GetBytes(name + "\0");
            SendPacket(0, PktSetClientName, bytes);
        }
    }

    // ------------------------------------------------------------------ API

    public int GetControllerCount()
    {
        lock (_io)
        {
            try
            {
                SendPacket(0, PktRequestControllerCount, null);
                var reply = ReceivePacket(PktRequestControllerCount);
                return reply is { Length: >= 4 } ? (int)BitConverter.ToUInt32(reply, 0) : 0;
            }
            catch { return 0; }
        }
    }

    public OpenRgbDevice? GetController(int index)
    {
        lock (_io)
        {
            try
            {
                // From protocol 1 onwards the request carries the negotiated version.
                byte[]? payload = ProtocolVersion >= 1 ? BitConverter.GetBytes(ProtocolVersion) : null;
                SendPacket((uint)index, PktRequestControllerData, payload);

                var blob = ReceivePacket(PktRequestControllerData);
                if (blob is null) return null;

                return ParseController(blob, index);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenRGB] controller {index} read failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Tells a controller how many LEDs are attached to one of its zones.
    ///
    /// Addressable headers (motherboard ARGB, fan hubs, case strips) cannot report their
    /// own length, so they sit at zero LEDs and appear controllable-but-empty until this
    /// is set. The device list has to be re-read afterwards, because the LED count and
    /// every index behind it changes.
    /// </summary>
    public bool ResizeZone(int deviceIndex, int zoneIndex, int newSize)
    {
        lock (_io)
        {
            try
            {
                var payload = new byte[8];
                BitConverter.TryWriteBytes(payload.AsSpan(0), zoneIndex);
                BitConverter.TryWriteBytes(payload.AsSpan(4), newSize);
                SendPacket((uint)deviceIndex, PktResizeZone, payload);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenRGB] resize failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Switches a device into its "Direct"/custom mode so per-LED writes stick.</summary>
    public void SetCustomMode(int index)
    {
        lock (_io)
        {
            try { SendPacket((uint)index, PktSetCustomMode, null); }
            catch { }
        }
    }

    public bool UpdateLeds(int index, ReadOnlySpan<Rgb24> colors)
    {
        lock (_io)
        {
            try
            {
                int count = colors.Length;
                // data_size(4) + num_colors(2) + colors(4 each)
                var payload = new byte[4 + 2 + count * 4];
                BitConverter.TryWriteBytes(payload.AsSpan(0), (uint)payload.Length);
                BitConverter.TryWriteBytes(payload.AsSpan(4), (ushort)count);

                for (int i = 0; i < count; i++)
                {
                    int p = 6 + i * 4;
                    payload[p + 0] = colors[i].R;
                    payload[p + 1] = colors[i].G;
                    payload[p + 2] = colors[i].B;
                    payload[p + 3] = 0;
                }

                SendPacket((uint)index, PktUpdateLeds, payload);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenRGB] update failed: {ex.Message}");

                // The socket is gone (OpenRGB was closed or crashed). Drop it so the
                // provider reports itself unavailable instead of failing every frame
                // while still claiming to be connected.
                Disconnect();
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ parsing

    private sealed class Cursor
    {
        public readonly byte[] Data;
        public int Pos;
        public Cursor(byte[] data, int pos) { Data = data; Pos = pos; }

        public bool Has(int n) => Pos + n <= Data.Length;

        public uint U32() { uint v = BitConverter.ToUInt32(Data, Pos); Pos += 4; return v; }
        public int I32() { int v = BitConverter.ToInt32(Data, Pos); Pos += 4; return v; }
        public ushort U16() { ushort v = BitConverter.ToUInt16(Data, Pos); Pos += 2; return v; }

        /// <summary>u16 length followed by that many bytes, including a trailing null.</summary>
        public string Str()
        {
            if (!Has(2)) return "";
            ushort len = U16();
            if (len == 0 || !Has(len)) return "";
            string s = Encoding.UTF8.GetString(Data, Pos, len);
            Pos += len;
            return s.TrimEnd('\0');
        }

        public void SkipColors()
        {
            if (!Has(2)) return;
            ushort n = U16();
            Pos += n * 4;
        }
    }

    private OpenRgbDevice? ParseController(byte[] blob, int index)
    {
        var c = new Cursor(blob, 0);
        var dev = new OpenRgbDevice { Index = index };

        if (!c.Has(8)) return null;
        c.U32();                                    // data_size, not needed
        dev.Type = (OpenRgbDeviceType)c.I32();
        dev.Name = c.Str();
        if (ProtocolVersion >= 1) dev.Vendor = c.Str();
        dev.Description = c.Str();
        dev.Version = c.Str();
        dev.Serial = c.Str();
        dev.Location = c.Str();

        // ---- modes ----
        if (!c.Has(6)) return dev;
        ushort numModes = c.U16();
        c.I32();                                    // active_mode

        for (int i = 0; i < numModes && c.Has(4); i++)
        {
            c.Str();                                // mode name
            c.I32();                                // mode value (present through protocol 5)
            c.U32();                                // flags
            c.U32();                                // speed_min
            c.U32();                                // speed_max
            if (ProtocolVersion >= 3) { c.U32(); c.U32(); }   // brightness_min / max
            c.U32();                                // colors_min
            c.U32();                                // colors_max
            c.U32();                                // speed
            if (ProtocolVersion >= 3) c.U32();       // brightness
            c.U32();                                // direction
            c.U32();                                // color_mode
            c.SkipColors();
        }

        // ---- zones ----
        if (!c.Has(2)) return dev;
        ushort numZones = c.U16();
        for (int i = 0; i < numZones && c.Has(2); i++)
        {
            var z = new OpenRgbZone { Index = i, Name = c.Str() };
            if (!c.Has(16)) break;
            z.Type = c.I32();
            z.LedsMin = c.U32();
            z.LedsMax = c.U32();
            z.LedsCount = c.U32();

            ushort matrixLen = c.U16();
            c.Pos += matrixLen;                     // matrix map, unused here

            if (ProtocolVersion >= 4 && c.Has(2))
            {
                ushort numSegments = c.U16();
                for (int s = 0; s < numSegments && c.Has(2); s++)
                {
                    c.Str();                        // segment name
                    if (!c.Has(12)) break;
                    c.I32();                        // segment type
                    c.U32();                        // start index
                    c.U32();                        // leds count
                }
            }

            dev.Zones.Add(z);
        }

        // ---- LEDs ----
        if (!c.Has(2)) return dev;
        ushort numLeds = c.U16();
        for (int i = 0; i < numLeds && c.Has(2); i++)
        {
            string name = c.Str();
            if (c.Has(4)) c.U32();                  // led value (present through protocol 5)
            dev.LedNames.Add(string.IsNullOrEmpty(name) ? $"LED {i + 1}" : name);
        }

        return dev;
    }

    public void Dispose() => Disconnect();
}
