using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SickRGB.Audio;

/// <summary>
/// Captures whatever the PC is currently playing, using WASAPI loopback.
///
/// Loopback taps the output mix, so it hears everything at once: music, a browser, a
/// game. Nothing is recorded from a microphone unless you choose one, and audio is never
/// written to disk or sent anywhere. Samples land in a small ring buffer and are
/// overwritten within a fraction of a second.
///
/// Written against the COM API directly rather than pulling in an audio library, to keep
/// the project free of dependencies.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    // ---------------------------------------------------------------- COM plumbing

    private const string ClsidMmDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E";

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                       IntPtr format, IntPtr audioSessionGuid);
        int GetBufferSize(out uint bufferFrameCount);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint framesRead, out uint flags,
                      out long devicePosition, out long qpcPosition);
        int ReleaseBuffer(uint framesWritten);
        int GetNextPacketSize(out uint packetFrames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    private const int DataFlowRender = 0;      // eRender: the output mix
    private const int DataFlowCapture = 1;     // eCapture: microphones and line-in
    private const int RoleConsole = 0;
    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int ClsCtxAll = 0x17;
    private const uint BufferFlagsSilent = 0x2;

    private const int WaveFormatPcm = 1;
    private const int WaveFormatIeeeFloat = 3;
    private const int WaveFormatExtensible = unchecked((short)0xFFFE);

    // ---------------------------------------------------------------- state

    /// <summary>
    /// Ring of recent mono samples. Sized generously so a stalled render thread cannot
    /// cause the analyser to read a torn window.
    /// </summary>
    private readonly float[] _ring = new float[16384];
    private int _writeIndex;
    private readonly object _ringLock = new();

    private Thread? _thread;
    private volatile bool _running;

    public int SampleRate { get; private set; } = 48000;

    /// <summary>True once audio has actually been seen, as opposed to merely started.</summary>
    public bool HasSignal { get; private set; }

    /// <summary>Set when capture could not start, for the UI to explain.</summary>
    public string? Error { get; private set; }

    /// <summary>Capture the microphone instead of what is playing.</summary>
    public bool UseMicrophone { get; set; }

    public void Start()
    {
        if (_running) return;
        _running = true;
        Error = null;

        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "SickRGB Audio",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        HasSignal = false;
    }

    /// <summary>Copies the most recent <paramref name="count"/> samples, oldest first.</summary>
    public bool ReadLatest(float[] destination, int count)
    {
        if (count > _ring.Length) return false;

        lock (_ringLock)
        {
            int start = _writeIndex - count;
            if (start < 0) start += _ring.Length;

            for (int i = 0; i < count; i++)
                destination[i] = _ring[(start + i) % _ring.Length];
        }
        return true;
    }

    private void CaptureLoop()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr formatPtr = IntPtr.Zero;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid(ClsidMmDeviceEnumerator))
                                 ?? throw new InvalidOperationException("audio device enumerator unavailable");
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

            // Loopback listens to an output device; a microphone is an input device.
            int flow = UseMicrophone ? DataFlowCapture : DataFlowRender;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, RoleConsole, out var device));

            var audioClientIid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(device.Activate(ref audioClientIid, ClsCtxAll, IntPtr.Zero, out object clientObj));
            client = (IAudioClient)clientObj;

            Marshal.ThrowExceptionForHR(client.GetMixFormat(out formatPtr));
            var format = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            SampleRate = format.nSamplesPerSec;

            int streamFlags = UseMicrophone ? 0 : StreamFlagsLoopback;
            const long bufferDuration = 2_000_000;   // 200 ms, in 100 ns units

            Marshal.ThrowExceptionForHR(
                client.Initialize(ShareModeShared, streamFlags, bufferDuration, 0, formatPtr, IntPtr.Zero));

            var captureIid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out object captureObj));
            capture = (IAudioCaptureClient)captureObj;

            Marshal.ThrowExceptionForHR(client.Start());

            bool isFloat = IsFloatFormat(format, formatPtr);
            int channels = Math.Max(1, (int)format.nChannels);
            int bytesPerSample = format.wBitsPerSample / 8;

            while (_running)
            {
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packetFrames));

                if (packetFrames == 0)
                {
                    // Nothing playing. Sleep briefly rather than spinning.
                    Thread.Sleep(5);
                    continue;
                }

                while (packetFrames > 0 && _running)
                {
                    Marshal.ThrowExceptionForHR(
                        capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));

                    if (frames > 0)
                    {
                        bool silent = (flags & BufferFlagsSilent) != 0;
                        AppendFrames(data, (int)frames, channels, bytesPerSample, isFloat, silent);
                    }

                    Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out packetFrames));
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Debug.WriteLine($"[Audio] capture failed: {ex}");
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>
    /// The mix format is usually 32-bit float, but WAVE_FORMAT_EXTENSIBLE hides the real
    /// type behind a sub-format GUID, so that has to be read too.
    /// </summary>
    private static bool IsFloatFormat(WaveFormatEx format, IntPtr formatPtr)
    {
        if (format.wFormatTag == WaveFormatIeeeFloat) return true;
        if (format.wFormatTag != WaveFormatExtensible) return false;

        try
        {
            // WAVEFORMATEXTENSIBLE: WAVEFORMATEX (18) + samples (2) + channel mask (4), then the GUID.
            var subFormat = Marshal.PtrToStructure<Guid>(formatPtr + 18 + 2 + 4);
            var floatSubtype = new Guid("00000003-0000-0010-8000-00AA00389B71");
            return subFormat == floatSubtype;
        }
        catch
        {
            return format.wBitsPerSample == 32;
        }
    }

    /// <summary>Mixes the captured frames down to mono and appends them to the ring.</summary>
    private unsafe void AppendFrames(IntPtr data, int frames, int channels, int bytesPerSample,
                                     bool isFloat, bool silent)
    {
        lock (_ringLock)
        {
            byte* src = (byte*)data;

            for (int f = 0; f < frames; f++)
            {
                float sum = 0;

                if (!silent)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        byte* sample = src + (f * channels + c) * bytesPerSample;

                        if (isFloat && bytesPerSample == 4) sum += *(float*)sample;
                        else if (bytesPerSample == 2) sum += *(short*)sample / 32768f;
                        else if (bytesPerSample == 4) sum += *(int*)sample / 2147483648f;
                    }
                    sum /= channels;
                }

                _ring[_writeIndex] = sum;
                _writeIndex = (_writeIndex + 1) % _ring.Length;

                if (!silent && Math.Abs(sum) > 0.0005f) HasSignal = true;
            }
        }
    }

    public void Dispose() => Stop();
}
