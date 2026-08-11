using System.Collections.Concurrent;
using System.Diagnostics;
using SickRGB.Capture;
using SickRGB.Core;
using SickRGB.Devices;

namespace SickRGB.Effects;

/// <summary>
/// The render thread. Turns effects into colours for every light on the canvas and
/// pushes them out to each device's provider.
///
/// Devices are batched into render groups: everything following the global effect
/// renders together (so a wave crosses device boundaries as one continuous motion),
/// while any device with its own effect gets its own group. Either way, every light's
/// coordinates come from the same shared canvas, so overrides stay spatially coherent.
/// </summary>
public sealed class EffectEngine : IDisposable
{
    private const string GlobalGroupKey = "\0global";

    private sealed class GroupMember
    {
        public required LightDevice Device { get; init; }
        public required int Offset { get; init; }
    }

    private sealed class RenderGroup
    {
        public required string Key { get; init; }
        public required Effect Effect { get; init; }
        public required List<GroupMember> Members { get; init; }
        public LightPoint[] Points = Array.Empty<LightPoint>();
        public RgbF[] Scratch = Array.Empty<RgbF>();
        public Rgb24[] Output = Array.Empty<Rgb24>();
    }

    private readonly AppSettings _settings;
    private readonly DeviceRegistry _registry;
    private readonly ConcurrentQueue<(ImpulseKind Kind, double AlongKeyboard)> _impulses = new();
    private readonly EffectContext _ctx = new();

    private List<RenderGroup> _groups = new();
    private ScreenSampler? _sampler;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _groupsDirty = true;

    /// <summary>
    /// Per-device output bookkeeping: what we last sent, when, and how many writes have
    /// failed in a row.
    ///
    /// The failure counter matters. A single failed write is not evidence a device has
    /// gone away - it can just be a busy handle or a dropped socket frame. Treating one
    /// failure as "device lost" and rescanning immediately caused a feedback loop: the
    /// rescan tore down and re-opened every device, which itself blanked the keyboard
    /// and re-ran its init handshake, which looked like a failure, which triggered
    /// another rescan.
    /// </summary>
    private sealed class DeviceOutput
    {
        public Rgb24[] LastSent = Array.Empty<Rgb24>();
        public DateTime LastPush = DateTime.MinValue;
        public int ConsecutiveFailures;
    }

    private readonly Dictionary<string, DeviceOutput> _outputs = new();

    /// <summary>Writes that must fail back-to-back before a device is considered lost.</summary>
    private const int FailuresBeforeRescan = 8;

    /// <summary>Re-send an unchanged frame at least this often, so nothing gets stuck.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);

    private DateTime _nextRescan = DateTime.MinValue;
    private int _rescanBackoffSeconds = 5;
    private volatile bool _rescanInFlight;

    /// <summary>Raised after each frame is written, for the UI preview.</summary>
    public event Action? FrameRendered;

    /// <summary>Raised when the device list changes.</summary>
    public event Action? DevicesChanged;

    public DeviceRegistry Registry => _registry;

    /// <summary>True while at least one active effect wants key presses.</summary>
    public bool NeedsInputHooks { get; private set; }

    public EffectEngine(AppSettings settings, DeviceRegistry registry)
    {
        _settings = settings;
        _registry = registry;
        _registry.DevicesChanged += () =>
        {
            _groupsDirty = true;
            DevicesChanged?.Invoke();
        };
    }

    /// <summary>Forces the render groups to be rebuilt on the next frame.</summary>
    public void Invalidate() => _groupsDirty = true;

    /// <summary>Recomputes canvas coordinates after a device has been moved.</summary>
    public void LayoutChanged()
    {
        _registry.RecomputeSpatial();
        _groupsDirty = true;
    }

    public async Task RescanAsync()
    {
        await _registry.RescanAsync(_settings).ConfigureAwait(false);
        _groupsDirty = true;
    }

    // ------------------------------------------------------------------ input

    /// <summary>Called from the keyboard hook. <paramref name="x"/> is 0..1 across the board.</summary>
    public void PushKey(double x)
    {
        if (_impulses.Count < 256) _impulses.Enqueue((ImpulseKind.Key, x));
    }

    /// <summary>Called from the mouse hook.</summary>
    public void PushClick()
    {
        if (_impulses.Count < 256) _impulses.Enqueue((ImpulseKind.MouseClick, 0.5));
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "SickRGB Render",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
    }

    // ------------------------------------------------------------------ grouping

    private void RebuildGroups()
    {
        var previous = _groups.ToDictionary(g => g.Key, g => g.Effect);
        var groups = new List<RenderGroup>();

        var devices = _registry.Devices.Where(d => d.Enabled && d.ZoneCount > 0).ToList();

        var globalMembers = new List<GroupMember>();
        int globalOffset = 0;

        foreach (var device in devices)
        {
            var ds = _settings.DeviceFor(device.Key);

            if (ds.SyncToGlobal)
            {
                globalMembers.Add(new GroupMember { Device = device, Offset = globalOffset });
                globalOffset += device.ZoneCount;
            }
            else
            {
                // Reuse the existing instance when the effect has not changed, so
                // in-flight ripples are not wiped out by an unrelated settings change.
                var effect = previous.TryGetValue(device.Key, out var e) && e.Id == ds.EffectId
                    ? e
                    : EffectLibrary.Create(ds.EffectId);

                groups.Add(new RenderGroup
                {
                    Key = device.Key,
                    Effect = effect,
                    Members = new List<GroupMember> { new() { Device = device, Offset = 0 } },
                });
            }
        }

        if (globalMembers.Count > 0)
        {
            var effect = previous.TryGetValue(GlobalGroupKey, out var g) && g.Id == _settings.GlobalEffectId
                ? g
                : EffectLibrary.Create(_settings.GlobalEffectId);

            groups.Insert(0, new RenderGroup
            {
                Key = GlobalGroupKey,
                Effect = effect,
                Members = globalMembers,
            });
        }

        // Allocate the per-group buffers once.
        foreach (var group in groups)
        {
            int total = group.Members.Sum(m => m.Device.ZoneCount);
            group.Points = new LightPoint[total];
            group.Scratch = new RgbF[total];
            group.Output = new Rgb24[total];
        }

        _groups = groups;
        NeedsInputHooks = groups.Any(g => g.Effect.IsReactive);

        // Drop bookkeeping for devices that are no longer present.
        var live = devices.Select(d => d.Key).ToHashSet();
        foreach (var stale in _outputs.Keys.Where(k => !live.Contains(k)).ToList())
            _outputs.Remove(stale);

        RefreshPoints();
    }

    /// <summary>Copies current canvas coordinates into each group's point array.</summary>
    private void RefreshPoints()
    {
        foreach (var group in _groups)
        {
            foreach (var member in group.Members)
            {
                var zones = member.Device.Zones;
                for (int i = 0; i < zones.Count; i++)
                {
                    var z = zones[i];
                    group.Points[member.Offset + i] = new LightPoint(z.NormX, z.NormY, z.WorldX, z.WorldY);
                }
            }
        }
    }

    // ------------------------------------------------------------------ render loop

    private void RenderLoop()
    {
        var clock = Stopwatch.StartNew();
        var uiClock = Stopwatch.StartNew();
        double previous = 0;

        while (_running)
        {
            // Floor of 5: slow buses genuinely cannot take more, and there is no reason
            // to burn frames the hardware will only throw away.
            int fps = Math.Clamp(_settings.TargetFps, 5, 144);
            double budgetMs = 1000.0 / fps;
            long frameStart = clock.ElapsedMilliseconds;

            try
            {
                if (_groupsDirty)
                {
                    _groupsDirty = false;
                    _registry.RecomputeSpatial();
                    RebuildGroups();
                }

                double now = clock.Elapsed.TotalSeconds;
                double delta = Math.Clamp(now - previous, 0, 0.25);
                previous = now;

                DispatchImpulses(now);
                RenderFrame(now, delta);

                if (uiClock.ElapsedMilliseconds >= 33)
                {
                    uiClock.Restart();
                    try { FrameRendered?.Invoke(); } catch { /* UI listener errors are not fatal */ }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EffectEngine] frame failed: {ex}");
            }

            int sleep = (int)Math.Round(budgetMs - (clock.ElapsedMilliseconds - frameStart));
            if (sleep > 0) Thread.Sleep(sleep);
        }

        _registry.ReleaseAll();
    }

    /// <summary>Turns queued input events into canvas-located impulses and fans them out.</summary>
    private void DispatchImpulses(double now)
    {
        while (_impulses.TryDequeue(out var evt))
        {
            var origin = evt.Kind == ImpulseKind.Key
                ? _registry.OriginForRole(DeviceRole.Keyboard, evt.AlongKeyboard)
                : _registry.OriginForRole(DeviceRole.Mouse);

            // With no matching device on the canvas, radiate from its centre instead.
            if (origin is null)
            {
                var (minX, minY, maxX, maxY) = _registry.Bounds;
                origin = ((minX + maxX) / 2, (minY + maxY) / 2);
            }

            var impulse = new Impulse(origin.Value.X, origin.Value.Y, now, evt.Kind);
            foreach (var group in _groups)
            {
                if (group.Effect.IsReactive) group.Effect.OnImpulse(impulse);
            }
        }
    }

    private void RenderFrame(double now, double delta)
    {
        if (_groups.Count == 0) return;

        RefreshPoints();

        bool needsScreen = _groups.Any(g => g.Effect.UsesScreen);
        CaptureTarget? target = null;

        if (needsScreen)
        {
            _sampler ??= new ScreenSampler();
            target = ResolveTarget();
            if (target is not null) _sampler.Capture(target, _settings.Smoothing);
        }

        double brightness = Math.Clamp(_settings.Brightness, 0, 1);
        bool deviceLost = false;
        bool anyFailureThisFrame = false;

        foreach (var group in _groups)
        {
            var preset = group.Key == GlobalGroupKey
                ? _settings.PresetFor(group.Effect.Id)
                : _settings.PresetFor(group.Key, group.Effect.Id);

            _ctx.Time = now;
            _ctx.Delta = delta;
            _ctx.Colors = AppSettings.PaletteOf(preset);
            _ctx.Speed = preset.Speed;
            _ctx.Intensity = preset.Intensity;
            _ctx.Diagonal = _registry.Diagonal;
            _ctx.Sampler = _sampler;
            _ctx.Target = target;
            _ctx.Saturation = _settings.Saturation;
            _ctx.AmbientFloor = _settings.AmbientFloor;
            _ctx.AmbientUseCanvasMapping = _settings.AmbientUseCanvasMapping;

            Array.Clear(group.Scratch);
            group.Effect.Render(_ctx, group.Points, group.Scratch);

            for (int i = 0; i < group.Scratch.Length; i++)
                group.Output[i] = group.Scratch[i].ToRgb24(brightness);

            foreach (var member in group.Members)
            {
                var device = member.Device;
                var slice = new ReadOnlySpan<Rgb24>(group.Output, member.Offset, device.ZoneCount);

                // Mirror into the model so the UI preview and canvas can draw it, even
                // when the write itself is skipped because nothing changed.
                for (int i = 0; i < device.ZoneCount; i++)
                    device.Zones[i].Current = slice[i];

                if (!_outputs.TryGetValue(device.Key, out var output))
                {
                    output = new DeviceOutput();
                    _outputs[device.Key] = output;
                }

                // Only push when something actually changed, with a periodic keep-alive
                // so another process cannot leave a device stuck on a stale colour.
                bool changed = output.LastSent.Length != slice.Length;
                if (!changed)
                {
                    for (int i = 0; i < slice.Length; i++)
                        if (output.LastSent[i] != slice[i]) { changed = true; break; }
                }

                bool keepAliveDue = DateTime.UtcNow - output.LastPush > KeepAliveInterval;
                if (!changed && !keepAliveDue) continue;

                // Respect a device's own ceiling, so slow hardware is not flooded with
                // frames it cannot absorb.
                if (device.MaxUpdatesPerSecond > 0 && !keepAliveDue)
                {
                    double minGapMs = 1000.0 / device.MaxUpdatesPerSecond;
                    if ((DateTime.UtcNow - output.LastPush).TotalMilliseconds < minGapMs) continue;
                }

                if (_registry.Apply(device, slice))
                {
                    if (output.LastSent.Length != slice.Length) output.LastSent = new Rgb24[slice.Length];
                    slice.CopyTo(output.LastSent);
                    output.LastPush = DateTime.UtcNow;
                    output.ConsecutiveFailures = 0;
                }
                else
                {
                    anyFailureThisFrame = true;
                    if (++output.ConsecutiveFailures >= FailuresBeforeRescan) deviceLost = true;
                }
            }
        }

        // Everything wrote cleanly, so whatever went wrong earlier has recovered.
        if (!anyFailureThisFrame) _rescanBackoffSeconds = 5;

        // Only rescan once a device has failed repeatedly, and back off each time so a
        // permanently absent device cannot pin the CPU or keep resetting healthy ones.
        if (deviceLost && !_rescanInFlight && DateTime.UtcNow > _nextRescan)
        {
            _nextRescan = DateTime.UtcNow.AddSeconds(_rescanBackoffSeconds);
            _rescanBackoffSeconds = Math.Min(_rescanBackoffSeconds * 2, 60);
            _rescanInFlight = true;

            foreach (var o in _outputs.Values) o.ConsecutiveFailures = 0;

            _ = Task.Run(async () =>
            {
                try { await RescanAsync().ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[EffectEngine] rescan failed: {ex.Message}"); }
                finally { _rescanInFlight = false; }
            });
        }
    }

    private List<CaptureTarget>? _cachedTargets;
    private DateTime _targetsRefreshedAt = DateTime.MinValue;

    /// <summary>
    /// Resolves the ambient capture target, caching the monitor list.
    ///
    /// Enumerating displays is not free, and this runs inside the render loop - doing it
    /// on every frame was costing real time in ambient mode. Monitors change rarely, so a
    /// few seconds of staleness is harmless.
    /// </summary>
    private CaptureTarget? ResolveTarget()
    {
        if (_cachedTargets is null || (DateTime.UtcNow - _targetsRefreshedAt).TotalSeconds > 5)
        {
            _cachedTargets = ScreenSampler.EnumerateTargets();
            _targetsRefreshedAt = DateTime.UtcNow;
        }

        if (_cachedTargets.Count == 0) return null;
        return _cachedTargets.FirstOrDefault(t => t.Name == _settings.CaptureTargetName) ?? _cachedTargets[0];
    }

    public void Dispose()
    {
        Stop();
        _sampler?.Dispose();
        _sampler = null;
    }
}
