using System.Windows;
using System.Windows.Media;
using SickRGB.Core;
using SickRGB.Effects;

namespace SickRGB.Controls;

/// <summary>
/// A small strip of lights behind an effect's tile, running that effect.
///
/// It is not a picture of what the effect does; it is the effect, rendered into a row of
/// twenty-eight lights. So a wave really travels, a ripple really spreads from where a key
/// would have been, and the colours are the ones that effect is actually set to use. Nothing
/// here can drift out of step with the real thing, because there is only one implementation.
///
/// Deliberately faint. It sits behind the name and the description, and its job is to answer
/// "what does this one look like" at a glance while staying quiet enough to read over.
///
/// Three effects cannot be run this way: two of them need microphone or screen capture, and
/// the third needs a live OBS connection. Opening any of those to animate a tile would be a
/// plainly bad trade, so those three are given a stand-in that moves the way they do. They
/// are marked in <see cref="NeedsStandIn"/> rather than being guessed at case by case.
/// </summary>
public sealed class EffectPreview : FrameworkElement
{
    /// <summary>
    /// Points the effect is sampled at.
    ///
    /// Few on purpose. These become the stops of a gradient rather than blocks of colour,
    /// so the fewer there are the softer the result; enough to show a wave moving across,
    /// not so many that the tile turns into a row of stripes.
    /// </summary>
    private const int Cells = 9;

    /// <summary>
    /// How strongly the wash shows through.
    ///
    /// It sits under text that has to stay readable, so it cannot be full strength. It also
    /// has to be plainly visible, or the tile says nothing and the whole thing is just
    /// noise in the corner of the eye.
    /// </summary>
    private const double Strength = 0.62;

    /// <summary>
    /// Lifts the dim parts without blowing out the bright ones.
    ///
    /// Several effects spend most of their time near black, and taking brightness straight
    /// to opacity left those tiles looking empty. Curving it means a half-lit part of an
    /// effect reads as clearly present while a fully lit one is still the strongest thing
    /// on the tile.
    /// </summary>
    private const double Lift = 0.55;

    /// <summary>
    /// Seconds for a stop to travel most of the way to its new colour.
    ///
    /// This is what makes the tiles calm. Several effects are meant to be abrupt: a flash
    /// is a flash, and a heat map jumps with every key. Shown at full speed in a small
    /// rectangle behind a label, that reads as flickering rather than as character. Easing
    /// each stop toward its target keeps the shape and the colour of the effect while
    /// turning the hard edges into swells.
    /// </summary>
    private const double Ease = 0.26;

    private static readonly HashSet<string> NeedsStandIn = new() { "audio", "direction", "ambient" };

    /// <summary>Used only when an effect's own palette is entirely black. See the constructor.</summary>
    private static readonly Rgb24[] FallbackPalette =
    {
        Rgb24.FromHex("#FF2D3C"), Rgb24.FromHex("#FF9114"), Rgb24.FromHex("#3CE66E"),
        Rgb24.FromHex("#00C8FF"), Rgb24.FromHex("#DC46FF"),
    };

    private readonly Effect _effect;
    private readonly string _id;
    private readonly LightPoint[] _points = new LightPoint[Cells];
    private readonly RgbF[] _output = new RgbF[Cells];
    private readonly RgbF[] _eased = new RgbF[Cells];
    private readonly EffectContext _context = new();

    /// <summary>
    /// One brush, its stops updated in place.
    ///
    /// A gradient rather than a row of rectangles: the point of the tile is a soft
    /// impression of the effect, and hard edges between cells make it look like a chart.
    /// Rebuilding the brush every frame would allocate for every tile on the page, so the
    /// stops are mutated instead.
    /// </summary>
    private readonly LinearGradientBrush _wash;

    private double _time;
    private double _nextImpulse;
    private bool _running;
    private RgbF _restingGlow;

    /// <summary>
    /// One clock for every preview on the page.
    ///
    /// Thirteen tiles each driving their own timer would be thirteen wake-ups per frame for
    /// decoration. One tick invalidates them all instead.
    /// </summary>
    private static readonly List<EffectPreview> Live = new();
    private static System.Windows.Threading.DispatcherTimer? _clock;
    private static DateTime _last = DateTime.UtcNow;

    public EffectPreview(string effectId, Rgb24[] palette, double speed, double intensity)
    {
        _id = effectId;
        _effect = EffectLibrary.Create(effectId);

        IsHitTestVisible = false;

        _wash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };

        for (int i = 0; i < Cells; i++)
        {
            double x = Cells == 1 ? 0.5 : i / (double)(Cells - 1);

            // Laid out as one device so effects that treat each device separately, like the
            // visualiser, still fill the whole strip.
            _points[i] = new LightPoint(x, 0.5, x * 600, 300, x, Cells);
            _wash.GradientStops.Add(new GradientStop(Colors.Transparent, x));
        }

        // Some effects make their own colours and leave the shared palette black: the
        // ambient one takes them from the screen, the status one from its own slots. Using
        // that palette here would draw a black tile, so those fall back to something that
        // at least moves.
        bool blank = palette.All(c => c.R == 0 && c.G == 0 && c.B == 0);

        _context.Colors = blank ? FallbackPalette : palette;
        _context.Speed = speed;
        _context.Intensity = intensity;
        _context.Diagonal = 600;

        // The status effect reads its colours from the slots rather than the palette, so
        // the preview shows the ones actually configured.
        _context.ObsSlots = AppServices.Current.Settings.ObsSlots.ToArray();

        // Taken from the brightest colour in the effect's palette, not the first.
        //
        // The reactive effects deliberately start from black, that being what a keyboard
        // should look like when nothing is happening, so the first entry is the one colour
        // that cannot serve as a glow. The brightest is always something the effect
        // actually shows.
        var brightest = _context.Colors
            .OrderByDescending(c => Math.Max(c.R, Math.Max(c.G, c.B)))
            .First();

        _restingGlow = RgbF.From(brightest) * 0.30;

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_running) return;
        _running = true;
        Live.Add(this);

        if (_clock is not null) return;

        // Twenty a second. Fast enough that a wave looks like a wave, slow enough that a
        // page of them costs nothing worth measuring.
        _clock = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        _clock.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            double delta = Math.Clamp((now - _last).TotalSeconds, 0, 0.2);
            _last = now;

            // Over a copy: a preview can load or unload while the page is changing, and
            // that would otherwise modify the list underneath this loop.
            foreach (var preview in Live.ToArray()) preview.Advance(delta);
        };

        _clock.Start();
    }

    private void Detach()
    {
        if (!_running) return;
        _running = false;
        Live.Remove(this);

        if (Live.Count != 0) return;

        _clock?.Stop();
        _clock = null;
    }

    /// <summary>How far each stop moves toward its target this frame.</summary>
    private double _easeAmount = 0.2;

    private void Advance(double delta)
    {
        _time += delta;

        // Frame rate independent, so the calm looks the same whatever the timer manages.
        _easeAmount = 1.0 - Math.Exp(-delta / Ease);

        InvalidateVisual();
    }

    /// <summary>
    /// Corner radius to clip to.
    ///
    /// The strip bleeds to the edge of the tile, which has rounded corners and a one pixel
    /// border, so this is the tile's radius less that border: square corners drawn over a
    /// rounded card is exactly the sort of mismatch that reads as broken.
    /// </summary>
    public double CornerRadius { get; set; } = 7;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        Paint();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), CornerRadius, CornerRadius));

        // Eased back towards the top left, where the name and the tag sit, and full
        // strength away from them. Enough of a difference to keep the label crisp, not so
        // much that half the tile disappears.
        dc.PushOpacityMask(new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xA6, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 1),
            },
        });

        dc.DrawRectangle(_wash, null, new Rect(0, 0, width, height));

        dc.Pop();
        dc.Pop();
    }

    /// <summary>Runs one frame of the effect and eases it into the gradient.</summary>
    private void Paint()
    {
        if (NeedsStandIn.Contains(_id)) StandIn();
        else RunEffect();

        for (int i = 0; i < Cells; i++)
        {
            // A resting glow under everything.
            //
            // The reactive effects are black between presses, which is right on a keyboard
            // and useless on a tile: at one press every second and a half the tile would
            // spend most of its life looking switched off. Holding a floor under the
            // effect's own first colour keeps every tile present, and the pulses ride on
            // top of it. Effects that are already brighter than the floor are untouched.
            var floor = _restingGlow;
            var value = _output[i];

            _output[i] = new RgbF(Math.Max(value.R, floor.R),
                                  Math.Max(value.G, floor.G),
                                  Math.Max(value.B, floor.B));

            _eased[i] = _eased[i].Lerp(_output[i], _easeAmount);

            var c = _eased[i];

            byte r = (byte)Math.Clamp(c.R * 255, 0, 255);
            byte g = (byte)Math.Clamp(c.G * 255, 0, 255);
            byte b = (byte)Math.Clamp(c.B * 255, 0, 255);

            // Brightness carries the alpha as well as the colour, so a dark part of the
            // effect fades into the tile rather than painting it black.
            double level = Math.Clamp(Math.Max(c.R, Math.Max(c.G, c.B)), 0, 1);
            byte alpha = (byte)Math.Clamp(Math.Pow(level, Lift) * 255 * Strength, 0, 255);

            _wash.GradientStops[i].Color = Color.FromArgb(alpha, r, g, b);
        }
    }

    private void RunEffect()
    {
        _context.Time = _time;
        _context.Delta = 0.05;

        // Reactive effects show nothing until something happens, so the preview supplies
        // the key presses the user would otherwise have to make.
        if (_effect.IsReactive && _time >= _nextImpulse)
        {
            // One press every second and a half. Fast enough that the tile is clearly
            // reacting to something, slow enough that a flash reads as a pulse rather than
            // a strobe. Anything quicker turned this row of tiles into a fairground.
            _nextImpulse = _time + 1.5;

            // Drifting slowly rather than jumping about, so an effect that accumulates,
            // like the heat map, builds a warm area that moves instead of smearing evenly
            // across the whole strip.
            double at = 0.5 + 0.34 * Math.Sin(_time * 0.42);
            _effect.OnImpulse(new Impulse(at * 600, 300, _time, ImpulseKind.Key));
        }

        if (_id == "obs") _context.Obs = FakeObsState();

        Array.Clear(_output);

        try { _effect.Render(_context, _points, _output); }
        catch { Array.Clear(_output); }
    }

    /// <summary>
    /// A stream state that cycles, so the status lamps have something to show.
    ///
    /// The tile is an advertisement for the effect, and an honest one: these are the three
    /// states it reports, shown in turn.
    /// </summary>
    private SickRGB.Obs.ObsSnapshot FakeObsState()
    {
        double phase = _time % 6.0;

        return new SickRGB.Obs.ObsSnapshot
        {
            Connected = true,
            Streaming = phase > 1.0,
            ProgramScene = "Scene",
            InputMuted = new Dictionary<string, bool> { [""] = phase < 4.0 },
            InputActive = new Dictionary<string, bool> { [""] = phase > 2.0 },
        };
    }

    /// <summary>
    /// Movement standing in for the three effects that cannot run without hardware.
    ///
    /// Each one moves the way the real effect does, using that effect's own palette, so the
    /// tile still says something true about it.
    /// </summary>
    private void StandIn()
    {
        var colors = _context.Colors;

        for (int i = 0; i < Cells; i++)
        {
            double x = i / (double)(Cells - 1);
            RgbF colour;

            switch (_id)
            {
                // Bars rising and falling, faster at the bass end, like a spectrum.
                case "audio":
                {
                    double band = 0.5 + 0.5 * Math.Sin(_time * (2.2 + x * 5.0) + x * 9.0);
                    double level = band * (1.0 - x * 0.35);
                    colour = RgbF.From(colors[Math.Clamp((int)(x * 5), 0, 4)]) * level;
                    break;
                }

                // A single soft spot drifting from one side to the other.
                case "direction":
                {
                    double centre = 0.5 + 0.42 * Math.Sin(_time * 0.9);
                    double offset = (x - centre) / 0.22;
                    double spot = Math.Exp(-offset * offset);
                    colour = RgbF.From(colors[1]) * spot;
                    break;
                }

                // Broad blocks of colour easing into one another, the way a screen does.
                default:
                {
                    double t = _time * 0.35 + x * 1.4;
                    int a = (int)Math.Floor(t) % 5;
                    if (a < 0) a += 5;
                    int b = (a + 1) % 5;

                    double blend = t - Math.Floor(t);
                    blend = blend * blend * (3 - 2 * blend);   // ease, so nothing snaps

                    colour = RgbF.From(colors[a]).Lerp(RgbF.From(colors[b]), blend) * 0.85;
                    break;
                }
            }

            _output[i] = colour;
        }
    }
}
