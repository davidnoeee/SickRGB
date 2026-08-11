using SickRGB.Capture;
using SickRGB.Core;

namespace SickRGB.Effects;

/// <summary>
/// One light, as the effect pipeline sees it: where it sits on the shared canvas.
///
/// Normalised coordinates are handy for gradients that should span the whole
/// arrangement; world coordinates are what distance-based effects use, so a ripple
/// travels at a consistent physical speed regardless of how the canvas is shaped.
/// </summary>
public readonly struct LightPoint
{
    /// <summary>Position within the canvas bounding box, 0..1 on each axis.</summary>
    public readonly double X, Y;

    /// <summary>Position in layout units (millimetres).</summary>
    public readonly double WorldX, WorldY;

    /// <summary>
    /// Where this light sits across its own device, 0..1.
    ///
    /// Separate from <see cref="X"/> on purpose. Some effects want the whole desk as one
    /// canvas; others, like the music visualiser, want to treat each device as its own
    /// display so a keyboard shows a full spectrum while a mouse beside it does its own
    /// thing.
    /// </summary>
    public readonly double DeviceX;

    /// <summary>How many lights the owning device has, so an effect can adapt to it.</summary>
    public readonly int DeviceLightCount;

    /// <summary>The slice of the audio spectrum this light's device is set to show, 0..1.</summary>
    public readonly double BandLow, BandHigh;

    public LightPoint(double x, double y, double worldX, double worldY,
                      double deviceX = 0.5, int deviceLightCount = 1,
                      double bandLow = 0, double bandHigh = 1)
    {
        X = x; Y = y;
        WorldX = worldX; WorldY = worldY;
        DeviceX = deviceX;
        DeviceLightCount = deviceLightCount;
        BandLow = bandLow; BandHigh = bandHigh;
    }
}

public enum ImpulseKind { Key, MouseClick }

/// <summary>An event that reactive effects radiate from, located on the canvas.</summary>
public readonly struct Impulse
{
    public readonly double WorldX, WorldY;
    public readonly double Time;
    public readonly ImpulseKind Kind;

    public Impulse(double worldX, double worldY, double time, ImpulseKind kind)
    {
        WorldX = worldX; WorldY = worldY; Time = time; Kind = kind;
    }
}

/// <summary>
/// A bounded, self-expiring set of impulses.
///
/// Reactive effects are written as a sum over live impulses rather than as per-light
/// accumulators. That keeps them stateless with respect to how many lights exist, so
/// devices can appear or disappear mid-effect without any resizing.
/// </summary>
public sealed class ImpulseSet
{
    private const int MaxImpulses = 64;
    private readonly List<Impulse> _items = new(MaxImpulses);

    public IReadOnlyList<Impulse> Items => _items;

    public void Add(Impulse impulse)
    {
        if (_items.Count >= MaxImpulses) _items.RemoveAt(0);
        _items.Add(impulse);
    }

    public void Expire(double now, double lifetime)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (now - _items[i].Time > lifetime)
                _items.RemoveAt(i);
    }

    public void Clear() => _items.Clear();
}

/// <summary>Everything an effect needs to render one frame.</summary>
public sealed class EffectContext
{
    public double Time;
    public double Delta;

    /// <summary>Resolved palette (always 5 entries).</summary>
    public Rgb24[] Colors = new Rgb24[5];

    public double Speed = 0.5;
    public double Intensity = 0.6;

    /// <summary>
    /// Diagonal of the canvas in layout units. Distances are divided by this so an
    /// effect crosses the whole arrangement in the same time no matter its size.
    /// </summary>
    public double Diagonal = 1;

    // ---- ambient only ----
    public ScreenSampler? Sampler;
    public CaptureTarget? Target;
    public double Saturation = 1.35;
    public double AmbientFloor;
    public bool AmbientUseCanvasMapping = true;

    // ---- audio visualiser only ----
    public SickRGB.Audio.SpectrumAnalyzer? Spectrum;
    public SickRGB.Audio.AudioColourMode AudioColourMode;
    public SickRGB.Audio.AudioLayout AudioLayout;
    public double AudioFloor;

    /// <summary>False when nothing has been heard yet, so the UI can say so.</summary>
    public bool AudioHasSignal;

    /// <summary>Maps the 0..1 speed slider onto a useful multiplier.</summary>
    public double SpeedFactor => 0.25 + Speed * 2.75;

    /// <summary>Normalised distance from a point to an impulse: 0 at the origin, ~1 across the canvas.</summary>
    public double Distance(in LightPoint p, in Impulse i)
    {
        double dx = p.WorldX - i.WorldX;
        double dy = p.WorldY - i.WorldY;
        return Math.Sqrt(dx * dx + dy * dy) / Diagonal;
    }
}

/// <summary>Base class for every lighting effect.</summary>
public abstract class Effect
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>Labels for the palette entries this effect uses. Empty means it generates its own colours.</summary>
    public virtual string[] ColorLabels => Array.Empty<string>();

    public virtual bool IsReactive => false;
    public virtual bool UsesScreen => false;

    /// <summary>True if this effect listens to audio.</summary>
    public virtual bool UsesAudio => false;

    public virtual bool UsesSpeed => true;
    public virtual bool UsesIntensity => false;
    public virtual string IntensityLabel => "Intensity";

    /// <summary>Called on the render thread when a key or mouse event occurs.</summary>
    public virtual void OnImpulse(in Impulse impulse) { }

    /// <summary>Renders one frame. <paramref name="output"/> has one entry per point.</summary>
    public abstract void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output);

    public virtual void Reset() { }
}
