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

    // ------------------------------------------------- per-application loopback
    //
    // Windows can hand over the audio of one process rather than the whole mix, but only
    // through a different, asynchronous activation path: there is no endpoint to enumerate,
    // so the client is activated against a virtual device and told which process to follow.

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        int GetActivateResult(out int activateResult,
                              [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>
    /// Waits for the activation to come back. The call is asynchronous whether or not we
    /// want it to be, so the capture thread blocks on this rather than the whole app.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new(false);
        public object? Interface;
        public int Result = unchecked((int)0x80004005);   // E_FAIL until told otherwise

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out Result, out object iface);
                Interface = iface;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] activation callback failed: {ex.Message}");
            }
            finally
            {
                Done.Set();
            }
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTargetTree = 0;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const short VtBlob = 65;

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
    /// Rings of recent samples, left and right kept apart.
    ///
    /// Stereo is preserved rather than mixed down because the difference between the two
    /// channels is the only thing that carries direction, and averaging destroys it. The
    /// spectrum work averages them back together on read, which costs nothing.
    ///
    /// Sized generously so a stalled render thread cannot cause a torn read.
    /// </summary>
    private readonly float[] _ringLeft = new float[16384];
    private readonly float[] _ringRight = new float[16384];
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

    /// <summary>
    /// Listen to one process rather than everything. Zero means the whole output mix.
    ///
    /// Worth having for the directional readout: a notification or a music player
    /// arriving mid-game would otherwise drag the reading away from the game itself.
    /// </summary>
    public int TargetProcessId { get; set; }

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

    /// <summary>
    /// Copies the most recent <paramref name="count"/> samples as mono, oldest first.
    /// </summary>
    public bool ReadLatest(float[] destination, int count)
    {
        if (count > _ringLeft.Length) return false;

        lock (_ringLock)
        {
            int start = _writeIndex - count;
            if (start < 0) start += _ringLeft.Length;

            for (int i = 0; i < count; i++)
            {
                int index = (start + i) % _ringLeft.Length;
                destination[i] = (_ringLeft[index] + _ringRight[index]) * 0.5f;
            }
        }
        return true;
    }

    /// <summary>
    /// Copies the most recent samples with the channels kept apart, oldest first.
    /// Used for working out which side a sound came from.
    /// </summary>
    public bool ReadLatestStereo(float[] left, float[] right, int count)
    {
        if (count > _ringLeft.Length || left.Length < count || right.Length < count) return false;

        lock (_ringLock)
        {
            int start = _writeIndex - count;
            if (start < 0) start += _ringLeft.Length;

            for (int i = 0; i < count; i++)
            {
                int index = (start + i) % _ringLeft.Length;
                left[i] = _ringLeft[index];
                right[i] = _ringRight[index];
            }
        }
        return true;
    }

    private void CaptureLoop()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr formatPtr = IntPtr.Zero;
        IntPtr eventHandle = IntPtr.Zero;
        bool formatFromCoTaskMem = false;

        try
        {
            int processId = UseMicrophone ? 0 : TargetProcessId;

            if (processId != 0)
            {
                client = ActivateForProcess(processId, out formatPtr, out eventHandle);
                formatFromCoTaskMem = false;
            }
            else
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
                formatFromCoTaskMem = true;

                int streamFlags = UseMicrophone ? 0 : StreamFlagsLoopback;
                const long bufferDuration = 2_000_000;   // 200 ms, in 100 ns units

                Marshal.ThrowExceptionForHR(
                    client.Initialize(ShareModeShared, streamFlags, bufferDuration, 0, formatPtr, IntPtr.Zero));
            }

            var wave = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            SampleRate = wave.nSamplesPerSec;

            var captureIid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out object captureObj));
            capture = (IAudioCaptureClient)captureObj;

            Marshal.ThrowExceptionForHR(client.Start());

            bool isFloat = IsFloatFormat(wave, formatPtr);
            int channels = Math.Max(1, (int)wave.nChannels);
            int bytesPerSample = wave.wBitsPerSample / 8;

            while (_running)
            {
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packetFrames));

                if (packetFrames == 0)
                {
                    // Nothing playing. Wait to be told, or sleep briefly rather than spinning.
                    if (eventHandle != IntPtr.Zero) WaitForSingleObject(eventHandle, 100);
                    else Thread.Sleep(5);
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

            if (formatPtr != IntPtr.Zero)
            {
                if (formatFromCoTaskMem) Marshal.FreeCoTaskMem(formatPtr);
                else Marshal.FreeHGlobal(formatPtr);
            }

            if (eventHandle != IntPtr.Zero) CloseHandle(eventHandle);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>
    /// Activates a client that hears one process instead of the whole mix.
    ///
    /// Two things differ from ordinary loopback. There is no mix format to ask for, since
    /// there is no endpoint behind this, so the format is stated rather than discovered.
    /// And the stream must be event-driven, which is why an event handle comes back with it.
    /// </summary>
    private IAudioClient ActivateForProcess(int processId, out IntPtr formatPtr, out IntPtr eventHandle)
    {
        formatPtr = IntPtr.Zero;
        eventHandle = IntPtr.Zero;

        // AUDIOCLIENT_ACTIVATION_PARAMS: type, then the process and how to treat its tree.
        // Following the tree matters: a browser plays through a child process, so targeting
        // the window you picked would otherwise hear nothing.
        IntPtr blob = Marshal.AllocHGlobal(12);
        IntPtr propVariant = IntPtr.Zero;

        try
        {
            Marshal.WriteInt32(blob, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(blob, 4, processId);
            Marshal.WriteInt32(blob, 8, LoopbackModeIncludeTargetTree);

            // PROPVARIANT holding that blob: tag, then size and pointer at the union.
            propVariant = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, VtBlob);
            Marshal.WriteInt32(propVariant, 8, 12);
            Marshal.WriteIntPtr(propVariant, 16, blob);

            var handler = new ActivationHandler();
            var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

            ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice, ref iid, propVariant, handler, out _);

            if (!handler.Done.Wait(4000))
                throw new TimeoutException("Windows did not answer the request to capture that app.");

            Marshal.ThrowExceptionForHR(handler.Result);

            var client = handler.Interface as IAudioClient
                         ?? throw new InvalidOperationException("no audio client came back for that app");

            // Stated, not discovered: GetMixFormat is not implemented on this path.
            var wave = new WaveFormatEx
            {
                wFormatTag = WaveFormatIeeeFloat,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = 48000 * 8,
                cbSize = 0,
            };

            formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(wave, formatPtr, false);

            eventHandle = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            if (eventHandle == IntPtr.Zero) throw new InvalidOperationException("could not create the audio event");

            Marshal.ThrowExceptionForHR(client.Initialize(
                ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                2_000_000, 0, formatPtr, IntPtr.Zero));

            Marshal.ThrowExceptionForHR(client.SetEventHandle(eventHandle));

            return client;
        }
        finally
        {
            if (propVariant != IntPtr.Zero) Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(blob);
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

    /// <summary>
    /// Appends captured frames to the rings, keeping left and right separate.
    ///
    /// Surround layouts put the front pair first, so channel 0 and 1 are taken as left
    /// and right; a mono source is written to both sides.
    /// </summary>
    private unsafe void AppendFrames(IntPtr data, int frames, int channels, int bytesPerSample,
                                     bool isFloat, bool silent)
    {
        lock (_ringLock)
        {
            byte* src = (byte*)data;

            for (int f = 0; f < frames; f++)
            {
                float left = 0, right = 0;

                if (!silent)
                {
                    left = ReadSample(src, (f * channels) * bytesPerSample, bytesPerSample, isFloat);
                    right = channels > 1
                        ? ReadSample(src, (f * channels + 1) * bytesPerSample, bytesPerSample, isFloat)
                        : left;
                }

                _ringLeft[_writeIndex] = left;
                _ringRight[_writeIndex] = right;
                _writeIndex = (_writeIndex + 1) % _ringLeft.Length;

                if (!silent && (Math.Abs(left) > 0.0005f || Math.Abs(right) > 0.0005f))
                    HasSignal = true;
            }
        }
    }

    private static unsafe float ReadSample(byte* src, int offset, int bytesPerSample, bool isFloat)
    {
        byte* sample = src + offset;

        if (isFloat && bytesPerSample == 4) return *(float*)sample;
        if (bytesPerSample == 2) return *(short*)sample / 32768f;
        if (bytesPerSample == 4) return *(int*)sample / 2147483648f;
        return 0;
    }

    public void Dispose() => Stop();
}
