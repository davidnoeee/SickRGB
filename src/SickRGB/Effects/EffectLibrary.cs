using SickRGB.Audio;
using SickRGB.Core;

namespace SickRGB.Effects;

// =====================================================================================
//  Non-reactive effects
// =====================================================================================

/// <summary>One colour everywhere.</summary>
public sealed class StaticEffect : Effect
{
    public override string Id => "static";
    public override string Name => "Static";
    public override string Description => "One steady colour across every light.";
    public override string[] ColorLabels => new[] { "Colour" };
    public override bool UsesSpeed => false;

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        var c = RgbF.From(ctx.Colors[0]);
        for (int i = 0; i < output.Length; i++) output[i] = c;
    }
}

/// <summary>Five colours spread left to right across the whole arrangement.</summary>
public sealed class PaletteEffect : Effect
{
    public override string Id => "palette";
    public override string Name => "Palette";
    public override string Description =>
        "Five colours spread across your layout from left to right. On a keyboard on its own, that gives one colour per zone.";
    public override string[] ColorLabels => new[] { "Band 1", "Band 2", "Band 3", "Band 4", "Band 5" };
    public override bool UsesSpeed => false;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Blend";

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double blend = ctx.Intensity;

        for (int i = 0; i < points.Length; i++)
        {
            double t = Math.Clamp(points[i].X, 0, 1) * 4.0;      // 0..4 across five bands
            int lo = (int)Math.Floor(t);
            int hi = Math.Min(lo + 1, 4);
            double f = t - lo;

            var a = RgbF.From(ctx.Colors[lo]);
            var b = RgbF.From(ctx.Colors[hi]);

            // Blend 0 gives hard bands; blend 1 gives a smooth ramp.
            double mix = blend <= 0.001 ? (f < 0.5 ? 0 : 1) : Smoothstep(f, blend);
            output[i] = a.Lerp(b, mix);
        }
    }

    /// <summary>Sharpens the crossfade as blend approaches zero.</summary>
    private static double Smoothstep(double f, double blend)
    {
        double edge = 0.5 - blend * 0.5;
        double t = Math.Clamp((f - edge) / Math.Max(blend, 1e-4), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>A two-colour gradient at an adjustable angle.</summary>
public sealed class GradientEffect : Effect
{
    public override string Id => "gradient";
    public override string Name => "Gradient";
    public override string Description => "A blend between two colours, at any angle.";
    public override string[] ColorLabels => new[] { "From", "To" };
    public override bool UsesSpeed => false;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Angle";

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double angle = ctx.Intensity * Math.PI * 2;
        double ax = Math.Cos(angle), ay = Math.Sin(angle);

        var from = RgbF.From(ctx.Colors[0]);
        var to = RgbF.From(ctx.Colors[1]);

        for (int i = 0; i < points.Length; i++)
        {
            // Project onto the angle, then remap from [-1,1] to [0,1].
            double proj = (points[i].X - 0.5) * ax + (points[i].Y - 0.5) * ay;
            output[i] = from.Lerp(to, Math.Clamp(proj + 0.5, 0, 1));
        }
    }
}

/// <summary>Everything swells and fades together.</summary>
public sealed class BreathingEffect : Effect
{
    public override string Id => "breathing";
    public override string Name => "Breathing";
    public override string Description => "Every light fades in and out together, in a slow pulse.";
    public override string[] ColorLabels => new[] { "Colour" };

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double phase = ctx.Time * 0.55 * ctx.SpeedFactor;
        double level = 0.5 - 0.5 * Math.Cos(phase * Math.PI * 2);
        level = 0.06 + 0.94 * Math.Pow(level, 1.7);

        var c = RgbF.From(ctx.Colors[0]) * level;
        for (int i = 0; i < output.Length; i++) output[i] = c;
    }
}

/// <summary>Every light cycles the spectrum in unison.</summary>
public sealed class RainbowCycleEffect : Effect
{
    public override string Id => "rainbow";
    public override string Name => "Rainbow Cycle";
    public override string Description => "Every light moves through the spectrum in step.";

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        var c = RgbF.FromHsv(ctx.Time * 0.08 * ctx.SpeedFactor, 1.0, 1.0);
        for (int i = 0; i < output.Length; i++) output[i] = c;
    }
}

/// <summary>A spectrum scrolling across the arrangement.</summary>
public sealed class ColorWaveEffect : Effect
{
    public override string Id => "colorwave";
    public override string Name => "Colour Wave";
    public override string Description =>
        "A rainbow that drifts across everything, following how you have arranged your devices.";
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Spread";

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double hue = ctx.Time * 0.10 * ctx.SpeedFactor;
        double spread = 0.15 + ctx.Intensity * 1.35;

        for (int i = 0; i < points.Length; i++)
            output[i] = RgbF.FromHsv(hue + points[i].X * spread, 1.0, 1.0);
    }
}

// =====================================================================================
//  Reactive effects - these are what make the canvas arrangement matter
// =====================================================================================

/// <summary>A tight ring expanding outward from wherever the event happened.</summary>
public sealed class RippleEffect : Effect
{
    private readonly ImpulseSet _impulses = new();

    public override string Id => "ripple";
    public override string Name => "Ripple";
    public override string Description =>
        "Every key press or click sends a ring spreading outwards. Nearby lights catch it first, distant ones later.";
    public override string[] ColorLabels => new[] { "Background", "Ripple" };
    public override bool IsReactive => true;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Trail length";

    public override void Reset() => _impulses.Clear();
    public override void OnImpulse(in Impulse impulse) => _impulses.Add(impulse);

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double speed = 0.55 * ctx.SpeedFactor;          // canvas-widths per second
        double decay = 2.6 - ctx.Intensity * 1.9;
        double lifetime = Math.Clamp(6.0 / Math.Max(decay, 0.2), 1.0, 12.0);
        _impulses.Expire(ctx.Time, lifetime);

        var bg = RgbF.From(ctx.Colors[0]);
        var fg = RgbF.From(ctx.Colors[1]);
        const double width = 0.055;                     // ring thickness, canvas-relative

        for (int i = 0; i < points.Length; i++)
        {
            double energy = 0;
            foreach (var imp in _impulses.Items)
            {
                double age = ctx.Time - imp.Time;
                if (age < 0) continue;

                double d = ctx.Distance(points[i], imp) - age * speed;
                energy += Math.Exp(-(d * d) / (2 * width * width)) * Math.Exp(-age * decay);
            }
            output[i] = bg.Lerp(fg, Math.Clamp(energy, 0, 1));
        }
    }
}

/// <summary>A broad, soft wave rolling outward from each event.</summary>
public sealed class ReactiveWaveEffect : Effect
{
    private readonly ImpulseSet _impulses = new();

    public override string Id => "wave";
    public override string Name => "Reactive Wave";
    public override string Description =>
        "Typing or clicking rolls a broad wave out from that spot, washing across your other devices as it travels.";
    public override string[] ColorLabels => new[] { "Background", "Wave" };
    public override bool IsReactive => true;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Wave width";

    public override void Reset() => _impulses.Clear();
    public override void OnImpulse(in Impulse impulse) => _impulses.Add(impulse);

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double speed = 0.38 * ctx.SpeedFactor;
        const double decay = 1.05;
        double width = 0.07 + ctx.Intensity * 0.22;
        _impulses.Expire(ctx.Time, 6.0);

        var bg = RgbF.From(ctx.Colors[0]);
        var fg = RgbF.From(ctx.Colors[1]);

        for (int i = 0; i < points.Length; i++)
        {
            double energy = 0;
            foreach (var imp in _impulses.Items)
            {
                double age = ctx.Time - imp.Time;
                if (age < 0) continue;

                double d = ctx.Distance(points[i], imp) - age * speed;
                energy += Math.Exp(-(d * d) / (2 * width * width)) * Math.Exp(-age * decay);
            }
            output[i] = bg.Lerp(fg, Math.Clamp(energy, 0, 1));
        }
    }
}

/// <summary>Lights near the event flare instantly, then fall away.</summary>
public sealed class ReactiveFlashEffect : Effect
{
    private readonly ImpulseSet _impulses = new();

    public override string Id => "flash";
    public override string Name => "Reactive Flash";
    public override string Description =>
        "The lights nearest a key press or click flare at once, then fade. Crisp and direct, with no travel.";
    public override string[] ColorLabels => new[] { "Background", "Flash" };
    public override bool IsReactive => true;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Reach";

    public override void Reset() => _impulses.Clear();
    public override void OnImpulse(in Impulse impulse) => _impulses.Add(impulse);

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        double decay = 1.6 + ctx.SpeedFactor * 1.4;
        double reach = 0.05 + ctx.Intensity * 0.45;
        _impulses.Expire(ctx.Time, 4.0);

        var bg = RgbF.From(ctx.Colors[0]);
        var fg = RgbF.From(ctx.Colors[1]);

        for (int i = 0; i < points.Length; i++)
        {
            double energy = 0;
            foreach (var imp in _impulses.Items)
            {
                double age = ctx.Time - imp.Time;
                if (age < 0) continue;

                double d = ctx.Distance(points[i], imp) / reach;
                energy += Math.Exp(-d * d) * Math.Exp(-age * decay);
            }
            output[i] = bg.Lerp(fg, Math.Clamp(energy, 0, 1));
        }
    }
}

/// <summary>Lights warm up where activity happens and cool down when it stops.</summary>
public sealed class TypingHeatEffect : Effect
{
    private readonly ImpulseSet _impulses = new();

    public override string Id => "heat";
    public override string Name => "Activity Heat";
    public override string Description =>
        "Lights warm up where you are working and cool down when you stop. A live heat map of your hands.";
    public override string[] ColorLabels => new[] { "Cold", "Hot" };
    public override bool IsReactive => true;
    public override bool UsesIntensity => true;
    public override string IntensityLabel => "Heat per press";

    public override void Reset() => _impulses.Clear();
    public override void OnImpulse(in Impulse impulse) => _impulses.Add(impulse);

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        // Speed controls how fast it cools; slower speed means heat lingers.
        double cool = 0.30 * ctx.SpeedFactor;
        double gain = 0.20 + ctx.Intensity * 0.55;
        const double reach = 0.22;

        _impulses.Expire(ctx.Time, Math.Clamp(8.0 / Math.Max(cool, 0.05), 2.0, 30.0));

        var cold = RgbF.From(ctx.Colors[0]);
        var hot = RgbF.From(ctx.Colors[1]);

        for (int i = 0; i < points.Length; i++)
        {
            double heat = 0;
            foreach (var imp in _impulses.Items)
            {
                double age = ctx.Time - imp.Time;
                if (age < 0) continue;

                double d = ctx.Distance(points[i], imp) / reach;
                heat += gain * Math.Exp(-d * d) * Math.Exp(-age * cool);
            }
            output[i] = cold.Lerp(hot, Math.Clamp(heat, 0, 1));
        }
    }
}

// =====================================================================================
//  Audio visualiser
// =====================================================================================

/// <summary>Turns whatever is playing into light, one display per device.</summary>
public sealed class AudioVisualizerEffect : Effect
{
    /// <summary>
    /// Below this many lights, a device shows the average level of its range rather than
    /// trying to draw a spectrum. Three or four lights cannot describe a shape; asking
    /// them to just looks like noise.
    /// </summary>
    private const int MinLightsForSpectrum = 5;

    public override string Id => "audio";
    public override string Name => "Music Visualiser";
    public override string Description =>
        "Listens to whatever your PC is playing. Every device becomes its own display: one with "
      + "plenty of lights shows a spectrum, one with only a few pulses with the volume. Each device "
      + "can be set to a different part of the music on the Devices page.";

    public override string[] ColorLabels =>
        new[] { "Bass", "Low mids", "Mids", "High mids", "Treble" };

    public override bool UsesAudio => true;
    public override bool UsesSpeed => false;

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        var spectrum = ctx.Spectrum;
        if (spectrum is null)
        {
            for (int i = 0; i < output.Length; i++) output[i] = RgbF.Black;
            return;
        }

        double floor = Math.Clamp(ctx.AudioFloor, 0, 1);

        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            double low = point.BandLow;
            double high = Math.Max(point.BandHigh, low + 1e-4);

            double level;
            double frequency;

            if (point.DeviceLightCount >= MinLightsForSpectrum)
            {
                // Enough lights to draw with: lay this device's slice of the spectrum
                // across the device itself, not across the room.
                double across = SpreadPosition(point.DeviceX, ctx.AudioLayout);
                frequency = low + across * (high - low);
                level = spectrum.SampleAt(frequency);
            }
            else
            {
                // Too few lights for a shape, so show how loud this slice is. The colour
                // still comes from the middle of the slice, so a bass device reads as a
                // bass colour.
                frequency = (low + high) * 0.5;
                level = spectrum.AverageBetween(low, high);
            }

            // Lift the whole range rather than clamping, so the floor reads as a gentle
            // glow underneath the music instead of a hard cutoff.
            double lit = floor + (1.0 - floor) * level;

            // Colour always comes from the five stops, so what is on screen is always what
            // is being used. The mode only decides what those stops are laid out along:
            // frequency for a spectrum, loudness for a level meter.
            double index = ctx.AudioColourMode == AudioColourMode.Meter ? level : frequency;
            output[i] = PaletteColour(ctx.Colors, index) * lit;
        }
    }

    /// <summary>Blends the five colour stops across the frequency range.</summary>
    private static RgbF PaletteColour(Rgb24[] colours, double frequency)
    {
        double t = Math.Clamp(frequency, 0, 1) * (colours.Length - 1);
        int low = (int)Math.Floor(t);
        int high = Math.Min(low + 1, colours.Length - 1);
        return RgbF.From(colours[low]).Lerp(RgbF.From(colours[high]), t - low);
    }

    /// <summary>Where the bass sits across a single device.</summary>
    private static double SpreadPosition(double deviceX, AudioLayout layout) => layout switch
    {
        AudioLayout.LeftToRight => deviceX,
        AudioLayout.RightToLeft => 1.0 - deviceX,
        AudioLayout.BassInCentre => Math.Abs(deviceX - 0.5) * 2.0,
        _ => 1.0 - Math.Abs(deviceX - 0.5) * 2.0,
    };
}

// =====================================================================================
//  Screen ambient
// =====================================================================================

/// <summary>Every light mirrors the part of the screen matching its place on the canvas.</summary>
public sealed class AmbientEffect : Effect
{
    public override string Id => "ambient";
    public override string Name => "Screen Ambient";
    public override string Description =>
        "Every light picks up the colour of the part of your screen it sits nearest in your layout.";
    public override bool UsesScreen => true;
    public override bool UsesSpeed => false;

    public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
    {
        var sampler = ctx.Sampler;
        if (sampler is null || ctx.Target is null)
        {
            for (int i = 0; i < output.Length; i++) output[i] = RgbF.Black;
            return;
        }

        for (int i = 0; i < points.Length; i++)
        {
            // Without canvas mapping every light samples by horizontal position only,
            // which suits a single row of devices better than a 2D arrangement.
            double u = points[i].X;
            double v = ctx.AmbientUseCanvasMapping ? points[i].Y : 0.5;

            var c = sampler.ColorAt(u, v);

            if (Math.Abs(ctx.Saturation - 1.0) > 0.001)
            {
                var (h, s, val) = c.ToHsv();
                c = RgbF.FromHsv(h, Math.Clamp(s * ctx.Saturation, 0, 1), val);
            }

            if (ctx.AmbientFloor > 0)
            {
                var (h, s, val) = c.ToHsv();
                if (val < ctx.AmbientFloor) c = RgbF.FromHsv(h, s, ctx.AmbientFloor);
            }

            output[i] = c;
        }
    }
}

// =====================================================================================

public static class EffectLibrary
{
    /// <summary>Creates a fresh instance of every effect, in UI order.</summary>
    public static IReadOnlyList<Effect> CreateAll() => new Effect[]
    {
        new StaticEffect(),
        new PaletteEffect(),
        new GradientEffect(),
        new BreathingEffect(),
        new RainbowCycleEffect(),
        new ColorWaveEffect(),
        new RippleEffect(),
        new ReactiveWaveEffect(),
        new ReactiveFlashEffect(),
        new TypingHeatEffect(),
        new AudioVisualizerEffect(),
        new AmbientEffect(),
    };

    /// <summary>
    /// Creates a fresh instance by id. Each rendering group gets its own instance so
    /// that per-effect state never leaks between the global effect and device overrides.
    /// </summary>
    public static Effect Create(string id) =>
        CreateAll().FirstOrDefault(e => e.Id == id) ?? new StaticEffect();

    public static Effect Describe(string id) => Create(id);
}
