using SickRGB.Core;
using SickRGB.OpenRgb;

namespace SickRGB.Devices.Providers;

/// <summary>
/// Bridges to a running OpenRGB instance over its SDK port.
///
/// This is what gives the app reach beyond the devices we drive natively: RAM,
/// motherboard headers, GPUs, case fans, coolers and anything else OpenRGB supports
/// all show up here as ordinary devices, each LED becoming a zone on the canvas.
///
/// OpenRGB needs "Enable SDK Server" switched on (it is on by default). Devices that
/// sit behind SMBus/I2C - RAM and most motherboards - additionally require OpenRGB
/// itself to be running elevated; that is a limitation of hardware access on Windows,
/// not of this bridge.
/// </summary>
public sealed class OpenRgbProvider : ILightProvider
{
    private readonly OpenRgbClient _client = new();
    private string _reason = "Not connected. Set this up from Settings.";

    /// <summary>
    /// Devices already switched into direct mode.
    ///
    /// Some hardware - graphics cards especially - ignores per-LED writes until it is
    /// taken out of its stored effect. Asking once at discovery is not always enough,
    /// because the device may still be initialising, so the request is repeated with
    /// the first colour we actually send.
    /// </summary>
    private readonly HashSet<int> _directModeSet = new();

    /// <summary>
    /// Latest colour waiting to be written to one device.
    ///
    /// Writes go out on a background thread rather than inline, because they travel over
    /// a socket to OpenRGB and then onto real buses. Memory modules sit behind SMBus,
    /// where a single update can take tens of milliseconds; done inline that stalls the
    /// whole render loop and every other device with it.
    ///
    /// Only the newest frame per device is kept - there is no point queueing colours
    /// nobody will ever see, and dropping stale ones is what keeps slow devices current
    /// instead of falling further and further behind.
    /// </summary>
    private sealed class PendingWrite
    {
        public Rgb24[] Colors = Array.Empty<Rgb24>();
        public bool Dirty;
    }

    private readonly Dictionary<int, PendingWrite> _pending = new();
    private readonly object _pendingLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _writer;
    private volatile bool _writerRunning;

    public string Id => "openrgb";
    public string DisplayName => "OpenRGB";
    public string Description => "Memory, motherboard, graphics card, coolers and case fans, through the OpenRGB app.";
    public bool IsAvailable => _client.IsConnected;
    public string UnavailableReason => _client.IsConnected ? "" : _reason;

    /// <summary>Host/port of the OpenRGB SDK server.</summary>
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;

    /// <summary>
    /// Settings, used to remember which headers have already been given an automatic
    /// length so a deliberate choice is never overwritten later.
    /// </summary>
    public AppSettings? Settings { get; set; }

    /// <summary>
    /// Length assumed for an addressable header that has never been configured.
    ///
    /// The hardware genuinely cannot measure this, so something has to be assumed. A
    /// generous guess is the safer direction: addressing more LEDs than exist is
    /// harmless - the extra data goes nowhere - while guessing short leaves part of a
    /// strip permanently dark and looking broken.
    /// </summary>
    private const int AssumedHeaderLeds = 30;

    public Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<LightDevice>();
        _directModeSet.Clear();

        // Device indices are about to change, so anything still queued is stale.
        lock (_pendingLock) _pending.Clear();

        if (!_client.IsConnected && !_client.Connect(Host, Port))
        {
            _reason = "OpenRGB is not running. Set it up from Settings, or start it and press Rescan.";
            return Task.FromResult<IReadOnlyList<LightDevice>>(found);
        }

        int count = _client.GetControllerCount();
        if (count <= 0)
        {
            _reason = "Connected, but OpenRGB found no devices of its own.";
            return Task.FromResult<IReadOnlyList<LightDevice>>(found);
        }

        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var d = _client.GetController(i);
            if (d is null) continue;

            // An addressable header reports zero LEDs until told how long its strip is,
            // which would leave it invisible and dark. Give it a working default so the
            // lights simply come on.
            d = AutoSizeHeaders(i, d);

            var resizable = d.Zones.Where(z => z.IsResizable).ToList();

            // A device with no LEDs is normally uninteresting - but an addressable header
            // with nothing configured also reports zero, and dropping it here would make
            // it impossible to ever set its strip length. Keep those so they can be sized.
            if (d.LedCount == 0 && resizable.Count == 0) continue;

            // Ask the device for its direct/custom mode so per-LED writes take effect.
            _client.SetCustomMode(i);

            var (zones, width, height) = BuildZones(d);

            string vendor = string.IsNullOrWhiteSpace(d.Vendor) ? "" : d.Vendor + " ";
            string identity = string.IsNullOrWhiteSpace(d.Serial) ? d.Location : d.Serial;

            var device = new LightDevice
            {
                // Name + identity keeps the key stable across restarts even if
                // OpenRGB reorders its device list.
                Key = $"openrgb:{d.Name}:{identity}",
                Name = $"{vendor}{d.Name}".Trim(),
                ProviderId = Id,
                Role = MapRole(d.Type),
                Zones = zones,
                Details = d.LedCount == 0
                    ? $"OpenRGB  -  {d.Type}  -  no strip length set yet"
                    : $"OpenRGB  -  {d.Type}  -  {d.LedCount} LED{(d.LedCount == 1 ? "" : "s")}, {d.Zones.Count} zone{(d.Zones.Count == 1 ? "" : "s")}",
                Width = width,
                Height = height,
                Tag = i,

                // Memory sits on SMBus, which is far slower than the socket we hand the
                // colours to. OpenRGB accepts a write in well under a millisecond and
                // drains it to the bus in its own time, so pushing 60 frames a second at
                // it just builds a backlog and the modules end up showing colours from
                // seconds ago. A lower rate keeps what you see current.
                //
                // This is only a starting point - it can be changed per device.
                MaxUpdatesPerSecond = d.Type == OpenRgbDeviceType.Dram ? 8 : 0,
                DefaultMaxUpdatesPerSecond = d.Type == OpenRgbDeviceType.Dram ? 8 : 0,
            };

            foreach (var z in resizable)
            {
                device.ResizableHeaders.Add(new ResizableHeader
                {
                    ZoneIndex = z.Index,
                    Name = string.IsNullOrWhiteSpace(z.Name) ? $"Header {z.Index + 1}" : z.Name,
                    CurrentLeds = (int)z.LedsCount,
                    MinLeds = (int)z.LedsMin,
                    MaxLeds = (int)Math.Min(z.LedsMax, 1024),
                });
            }

            found.Add(device);
        }

        _reason = "";
        return Task.FromResult<IReadOnlyList<LightDevice>>(found);
    }

    /// <summary>
    /// Chooses a plausible physical shape per device class, so a 100-LED strip does not
    /// become a mile-wide line and a keyboard does not become a tiny square.
    /// Sizes are in millimetres to match the rest of the canvas.
    /// </summary>
    /// <summary>
    /// Gives any never-configured addressable header a default strip length.
    ///
    /// Done once per header and remembered, so if the length is corrected by hand - or
    /// deliberately set to zero to switch a header off - that choice is never undone on
    /// a later scan.
    /// </summary>
    private OpenRgbDevice AutoSizeHeaders(int index, OpenRgbDevice device)
    {
        if (Settings is null) return device;

        bool changed = false;

        foreach (var zone in device.Zones.Where(z => z.IsResizable && z.LedsCount == 0))
        {
            string key = $"{device.Name}|{device.Location}|{zone.Index}";
            if (Settings.AutoSizedHeaders.Contains(key)) continue;

            int guess = (int)Math.Clamp(AssumedHeaderLeds, Math.Max(zone.LedsMin, 1), zone.LedsMax);
            if (guess <= 0) continue;

            if (_client.ResizeZone(index, zone.Index, guess))
            {
                Settings.AutoSizedHeaders.Add(key);
                changed = true;
            }
        }

        if (!changed) return device;
        Settings.Save();

        // The resize is applied on OpenRGB's own thread, so wait for the new layout.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(250);
            var refreshed = _client.GetController(index);
            if (refreshed is not null && refreshed.LedCount > device.LedCount) return refreshed;
        }

        return _client.GetController(index) ?? device;
    }

    private static (List<LightZone> Zones, double Width, double Height) BuildZones(OpenRgbDevice d)
    {
        int n = Math.Max(1, d.LedCount);

        (int maxCols, double targetWidth) = d.Type switch
        {
            OpenRgbDeviceType.Keyboard => (21, 440.0),
            OpenRgbDeviceType.Mouse => (3, 68.0),
            OpenRgbDeviceType.Mousemat => (12, 350.0),
            OpenRgbDeviceType.Dram => (10, 133.0),
            OpenRgbDeviceType.Gpu => (12, 280.0),
            OpenRgbDeviceType.Motherboard => (8, 244.0),
            OpenRgbDeviceType.Case => (8, 200.0),
            OpenRgbDeviceType.Cooler => (8, 140.0),
            OpenRgbDeviceType.LedStrip => (20, 500.0),
            OpenRgbDeviceType.Headset or OpenRgbDeviceType.HeadsetStand => (4, 120.0),
            _ => (10, 200.0),
        };

        int cols = Math.Min(n, maxCols);
        int rows = (int)Math.Ceiling(n / (double)cols);
        double cell = targetWidth / cols;

        var zones = LightDevice.GridZones(n, "LED", cols, cell);

        // Give single-row devices a bit of visual height so they are easy to grab.
        double height = rows * cell;
        if (rows == 1) height = Math.Max(cell, 34);
        for (int i = 0; i < zones.Count && rows == 1; i++) zones[i].Height = height;

        return (zones, cols * cell, height);
    }

    private static DeviceRole MapRole(OpenRgbDeviceType t) => t switch
    {
        OpenRgbDeviceType.Keyboard => DeviceRole.Keyboard,
        OpenRgbDeviceType.Mouse => DeviceRole.Mouse,
        OpenRgbDeviceType.Mousemat => DeviceRole.Mousepad,
        OpenRgbDeviceType.Motherboard => DeviceRole.Motherboard,
        OpenRgbDeviceType.Dram => DeviceRole.Memory,
        OpenRgbDeviceType.Gpu => DeviceRole.Gpu,
        OpenRgbDeviceType.Cooler => DeviceRole.Cooler,
        OpenRgbDeviceType.Case => DeviceRole.Case,
        OpenRgbDeviceType.Headset or OpenRgbDeviceType.HeadsetStand => DeviceRole.Headset,
        OpenRgbDeviceType.LedStrip => DeviceRole.LightStrip,
        _ => DeviceRole.Other,
    };

    /// <summary>
    /// Hands the frame to the background writer. Returns immediately - the render loop
    /// is never made to wait on a slow bus.
    /// </summary>
    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors)
    {
        if (device.Tag is not int index || !_client.IsConnected) return false;

        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(index, out var slot))
            {
                slot = new PendingWrite();
                _pending[index] = slot;
            }

            if (slot.Colors.Length != zoneColors.Length) slot.Colors = new Rgb24[zoneColors.Length];
            zoneColors.CopyTo(slot.Colors);
            slot.Dirty = true;
        }

        EnsureWriter();
        _wake.Set();
        return true;
    }

    private void EnsureWriter()
    {
        if (_writerRunning) return;

        _writerRunning = true;
        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "OpenRGB writer",
        };
        _writer.Start();
    }

    private void WriteLoop()
    {
        var scratch = new Dictionary<int, Rgb24[]>();

        while (_writerRunning)
        {
            _wake.WaitOne(200);

            scratch.Clear();
            lock (_pendingLock)
            {
                foreach (var (index, slot) in _pending)
                {
                    if (!slot.Dirty) continue;
                    slot.Dirty = false;
                    scratch[index] = (Rgb24[])slot.Colors.Clone();
                }
            }

            foreach (var (index, colors) in scratch)
            {
                if (!_writerRunning || !_client.IsConnected) break;

                try
                {
                    // Devices that hold a stored effect ignore per-LED writes until they
                    // are switched to direct mode, and asking at discovery can land while
                    // the device is still starting up. Repeat it with the first colour.
                    if (_directModeSet.Add(index)) _client.SetCustomMode(index);

                    _client.UpdateLeds(index, colors);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OpenRGB] write to {index} failed: {ex.Message}");
                }
            }
        }
    }

    private void StopWriter()
    {
        _writerRunning = false;
        try { _wake.Set(); } catch { }
        _writer?.Join(1000);
        _writer = null;
    }

    /// <summary>
    /// Sets how many LEDs are attached to an addressable header, and waits until the
    /// change has actually landed.
    ///
    /// OpenRGB applies a resize asynchronously - it rebuilds the controller and its LED
    /// list on its own thread. Re-reading straight away returns the old size, which makes
    /// the whole thing look like it did nothing, so poll until the new size is reported.
    /// </summary>
    public async Task<bool> ResizeHeaderAsync(LightDevice device, int zoneIndex, int ledCount, CancellationToken ct = default)
    {
        if (device.Tag is not int index || !_client.IsConnected) return false;
        if (!_client.ResizeZone(index, zoneIndex, ledCount)) return false;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            var refreshed = _client.GetController(index);
            var zone = refreshed?.Zones.FirstOrDefault(z => z.Index == zoneIndex);
            if (zone is not null && zone.LedsCount == ledCount) return true;
        }

        return false;
    }

    /// <summary>
    /// Turns the device off on the way out.
    ///
    /// OpenRGB holds whatever colour it was last given, so simply stopping would leave
    /// these lights stuck on the last frame - and any colour still set inside OpenRGB
    /// itself would blend back in. Writing black leaves them dark and predictable.
    /// This one goes out directly rather than through the queue, since the writer thread
    /// is on its way down.
    /// </summary>
    public void Release(LightDevice device)
    {
        if (device.Tag is not int index || !_client.IsConnected) return;

        try
        {
            int count = Math.Max(device.ZoneCount, 1);
            var off = new Rgb24[count];
            _client.UpdateLeds(index, off);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenRGB] could not blank {device.Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopWriter();
        _client.Dispose();
        _wake.Dispose();
    }
}
