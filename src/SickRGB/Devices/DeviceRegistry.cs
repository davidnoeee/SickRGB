using SickRGB.Core;
using SickRGB.Devices.Providers;

namespace SickRGB.Devices;

/// <summary>
/// Aggregates every provider into one device list, positions devices on the shared
/// canvas, and keeps the spatial coordinates that effects run on up to date.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    private readonly List<ILightProvider> _providers = new();
    private readonly List<LightDevice> _devices = new();
    private readonly object _lock = new();

    public IReadOnlyList<ILightProvider> Providers => _providers;

    /// <summary>Snapshot of the current devices. Safe to enumerate from the UI thread.</summary>
    public IReadOnlyList<LightDevice> Devices
    {
        get { lock (_lock) return _devices.ToArray(); }
    }

    /// <summary>Bounding box of every enabled zone, in layout units.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds { get; private set; } = (0, 0, 1, 1);

    /// <summary>Diagonal of the bounding box. Effects use it to keep speed consistent.</summary>
    public double Diagonal { get; private set; } = 1;

    public event Action? DevicesChanged;

    public DeviceRegistry()
    {
        _providers.Add(new MagmaProvider());
        _providers.Add(new ViaKeyboardProvider());
        _providers.Add(new LogitechProvider());
        _providers.Add(new OpenRgbProvider());
    }

    public T? GetProvider<T>() where T : class, ILightProvider =>
        _providers.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Serialises discovery. Rescans are triggered from several places - the Rescan
    /// button, the setup wizard, and the render thread after repeated write failures -
    /// and two running at once would race to open the same device handles.
    /// </summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>Re-runs discovery across every provider and re-applies saved layout.</summary>
    public async Task RescanAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!await _scanGate.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            return;

        try
        {
            var discovered = new List<LightDevice>();

            foreach (var provider in _providers)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var list = await provider.DiscoverAsync(ct).ConfigureAwait(false);
                    discovered.AddRange(list);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Registry] {provider.Id} discovery failed: {ex.Message}");
                }
            }

            lock (_lock)
            {
                _devices.Clear();
                _devices.AddRange(discovered);
                ApplySettings(settings);
            }

            RecomputeSpatial();
        }
        finally
        {
            _scanGate.Release();
        }

        DevicesChanged?.Invoke();
    }

    /// <summary>Restores saved position/enabled/role, or assigns a sensible default.</summary>
    private void ApplySettings(AppSettings settings)
    {
        var roleCounters = new Dictionary<DeviceRole, int>();

        foreach (var device in _devices)
        {
            var saved = settings.DeviceFor(device.Key);

            if (saved.HasPlacement)
            {
                device.X = saved.X;
                device.Y = saved.Y;
                device.Rotation = saved.Rotation;
                device.Scale = saved.Scale <= 0 ? 1.0 : saved.Scale;
                device.Enabled = saved.Enabled;
                device.Reversed = saved.Reversed;
                if (saved.Role is { } role) device.Role = role;
            }
            else
            {
                roleCounters.TryGetValue(device.Role, out int n);
                roleCounters[device.Role] = n + 1;

                var (x, y) = DefaultPlacement(device.Role, n);
                device.X = x;
                device.Y = y;

                saved.X = x;
                saved.Y = y;
                saved.HasPlacement = true;
                saved.Enabled = device.Enabled;
                saved.Role = device.Role;
            }

            // A chosen update rate overrides the provider's suggestion. Applied after the
            // branch above so it holds whether or not this device has been seen before.
            if (saved.UpdateRate is { } rate) device.MaxUpdatesPerSecond = Math.Max(0, rate);
        }
    }

    /// <summary>
    /// Rough desk layout used the first time a device is seen: keyboard front and centre,
    /// mouse to its right, tower components behind. Everything is draggable afterwards.
    /// </summary>
    private static (double X, double Y) DefaultPlacement(DeviceRole role, int indexWithinRole) => role switch
    {
        DeviceRole.Keyboard => (260, 560 + indexWithinRole * 160),
        DeviceRole.Mouse => (760, 570 + indexWithinRole * 90),
        DeviceRole.Mousepad => (240, 540 + indexWithinRole * 40),
        DeviceRole.Motherboard => (380, 120 + indexWithinRole * 60),
        DeviceRole.Memory => (700, 90 + indexWithinRole * 46),
        DeviceRole.Gpu => (380, 390 + indexWithinRole * 60),
        DeviceRole.Cooler => (660, 250 + indexWithinRole * 150),
        DeviceRole.Fan => (170, 260 + indexWithinRole * 150),
        DeviceRole.Case => (120, 120 + indexWithinRole * 210),
        DeviceRole.Headset => (900, 220 + indexWithinRole * 130),
        DeviceRole.LightStrip => (100, 40 + indexWithinRole * 60),
        _ => (880, 400 + indexWithinRole * 90),
    };

    /// <summary>
    /// Puts every device back to its default desk position, discarding manual placement.
    /// </summary>
    public void AutoArrange(AppSettings settings)
    {
        var roleCounters = new Dictionary<DeviceRole, int>();

        foreach (var device in Devices)
        {
            roleCounters.TryGetValue(device.Role, out int n);
            roleCounters[device.Role] = n + 1;

            var (x, y) = DefaultPlacement(device.Role, n);
            device.X = x;
            device.Y = y;

            var saved = settings.DeviceFor(device.Key);
            saved.X = x;
            saved.Y = y;
            saved.HasPlacement = true;
        }

        settings.Save();
        RecomputeSpatial();
    }

    /// <summary>
    /// Recomputes every zone's world position and its normalised position inside the
    /// overall bounding box. Effects are written against these coordinates, which is
    /// what makes a wave travel correctly from one device to another.
    /// </summary>
    public void RecomputeSpatial()
    {
        LightDevice[] snapshot;
        lock (_lock) snapshot = _devices.ToArray();

        foreach (var d in snapshot) d.UpdateWorldPositions();

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var d in snapshot)
        {
            if (!d.Enabled) continue;
            foreach (var z in d.Zones)
            {
                any = true;
                minX = Math.Min(minX, z.WorldX);
                minY = Math.Min(minY, z.WorldY);
                maxX = Math.Max(maxX, z.WorldX);
                maxY = Math.Max(maxY, z.WorldY);
            }
        }

        // A single light, or several stacked in one spot, gives a zero-size box. Pad it so
        // normalised coordinates stay finite instead of collapsing to a divide by zero.
        if (any && (maxX - minX) < 1) { minX -= 50; maxX += 50; }
        if (any && (maxY - minY) < 1) { minY -= 50; maxY += 50; }

        if (!any) { minX = minY = 0; maxX = maxY = 1; }

        // Never divide by zero when everything sits on one line.
        double spanX = Math.Max(maxX - minX, 1e-6);
        double spanY = Math.Max(maxY - minY, 1e-6);

        Bounds = (minX, minY, maxX, maxY);
        Diagonal = Math.Max(Math.Sqrt(spanX * spanX + spanY * spanY), 1e-6);

        foreach (var d in snapshot)
        {
            foreach (var z in d.Zones)
            {
                z.NormX = (z.WorldX - minX) / spanX;
                z.NormY = (z.WorldY - minY) / spanY;
            }
        }
    }

    /// <summary>Finds the on-canvas point a key press or click should radiate from.</summary>
    public (double X, double Y)? OriginForRole(DeviceRole role, double alongDevice = 0.5)
    {
        foreach (var d in Devices)
        {
            if (!d.Enabled || d.Role != role) continue;
            double x = d.X + Math.Clamp(alongDevice, 0, 1) * d.Width;
            double y = d.Y + d.Height / 2.0;
            return (x, y);
        }
        return null;
    }

    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> colors)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == device.ProviderId);
        if (provider is null) return false;
        return provider.Apply(device, colors);
    }

    public void ReleaseAll()
    {
        foreach (var d in Devices)
        {
            var provider = _providers.FirstOrDefault(p => p.Id == d.ProviderId);
            try { provider?.Release(d); } catch { }
        }
    }

    public void Dispose()
    {
        ReleaseAll();
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { }
        }
        _providers.Clear();
    }
}
