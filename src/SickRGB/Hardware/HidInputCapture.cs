using System.Diagnostics;

namespace SickRGB.Hardware;

/// <summary>
/// Logs the reports a device sends while you use it.
///
/// This is the part of diagnosis that cannot be done from a static dump: it shows which
/// collection carries vendor traffic, what a lighting command looks like coming back, and
/// whether a device reports state changes at all.
///
/// PRIVACY: on a keyboard, reports contain the keys being pressed. Windows reserves the
/// standard keyboard collection for the system, so ordinary typing is usually not readable
/// here, but a vendor collection can carry anything the manufacturer chose to put on it,
/// and that may include key data. Capture therefore only runs while explicitly started,
/// and the caller is expected to say so plainly.
///
/// Nothing is written to any device, and nothing is saved anywhere unless the log is saved
/// deliberately.
/// </summary>
internal sealed class HidInputCapture : IDisposable
{
    private sealed class Reader
    {
        public required Thread Thread { get; init; }
        public required SafeFileHandleEx Handle { get; init; }
    }

    private readonly List<Reader> _readers = new();
    private volatile bool _running;

    /// <summary>Raised for each line of output. Marshal to the UI thread yourself.</summary>
    public event Action<string>? Logged;

    public bool IsRunning => _running;

    /// <summary>
    /// Starts listening on the given collections. Returns how many could actually be
    /// opened; Windows refuses some, which is itself worth reporting.
    /// </summary>
    public int Start(IEnumerable<HidNative.HidCollection> collections)
    {
        Stop();
        _running = true;
        int started = 0;

        foreach (var collection in collections)
        {
            if (collection.InputReportLength <= 0) continue;

            var handle = HidNative.CreateFile(collection.Path,
                HidNative.GENERIC_READ, HidNative.FILE_SHARE_READ_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                Logged?.Invoke($"[skipped] usage 0x{collection.UsagePage:X4}/0x{collection.Usage:X2} " +
                               "could not be opened for reading (held by another process, or reserved by Windows)");
                continue;
            }

            var captured = collection;
            var thread = new Thread(() => ReadLoop(captured, handle))
            {
                IsBackground = true,
                Name = $"HID capture {collection.UsagePage:X4}",
            };

            _readers.Add(new Reader { Thread = thread, Handle = handle });
            thread.Start();
            started++;

            Logged?.Invoke($"[listening] usage 0x{collection.UsagePage:X4}/0x{collection.Usage:X2}, " +
                           $"{collection.InputReportLength} byte reports");
        }

        return started;
    }

    private void ReadLoop(HidNative.HidCollection collection, SafeFileHandleEx handle)
    {
        var buffer = new byte[collection.InputReportLength];
        var clock = Stopwatch.StartNew();

        byte[]? previous = null;
        int repeats = 0;

        // A held key repeats forever; collapsing repeats keeps the log readable without
        // hiding that they happened.
        while (_running)
        {
            bool ok;
            try { ok = HidNative.ReadFile(handle, buffer, buffer.Length, out int read, IntPtr.Zero) && read > 0; }
            catch { break; }

            if (!_running) break;
            if (!ok) { Thread.Sleep(5); continue; }

            if (previous is not null && buffer.AsSpan().SequenceEqual(previous))
            {
                repeats++;
                continue;
            }

            if (repeats > 0)
            {
                Logged?.Invoke($"           ... previous report repeated {repeats} more times");
                repeats = 0;
            }

            previous = buffer.ToArray();
            Logged?.Invoke($"[{clock.Elapsed.TotalSeconds,8:0.000}] " +
                           $"0x{collection.UsagePage:X4}/0x{collection.Usage:X2}  " +
                           HidDiagnostics.Hex(previous, 32));
        }
    }

    public void Stop()
    {
        if (!_running && _readers.Count == 0) return;
        _running = false;

        foreach (var reader in _readers)
        {
            // A blocked read has to be cancelled, or the thread never returns.
            try { HidNative.CancelIoEx(reader.Handle, IntPtr.Zero); } catch { }
        }

        foreach (var reader in _readers)
        {
            reader.Thread.Join(700);
            reader.Handle.Dispose();
        }

        _readers.Clear();
    }

    public void Dispose() => Stop();
}
