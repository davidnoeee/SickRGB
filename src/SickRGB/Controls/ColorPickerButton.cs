using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SickRGB.Core;

namespace SickRGB.Controls;

/// <summary>
/// A colour swatch with a caption that opens a Fluent-styled HSV picker.
///
/// Built in code rather than XAML so it can be dropped into any page without
/// resource plumbing, and so several can be generated dynamically for effects
/// with different numbers of palette entries.
/// </summary>
public sealed class ColorPickerButton : StackPanel
{
    private readonly Border _swatch;
    private readonly TextBlock _caption;
    private readonly Popup _popup;

    private Slider _hue = null!, _sat = null!, _val = null!;
    private Border _preview = null!, _satTrack = null!, _valTrack = null!;
    private TextBox _hex = null!;

    private Rgb24 _color;
    private bool _updating;

    private static readonly string[] Presets =
    {
        "#FF0000", "#FF5A1F", "#FFA000", "#FFE000", "#8CFF00", "#00FF44",
        "#00FFC8", "#00D0FF", "#0066FF", "#5B2BFF", "#B300FF", "#FF00A6",
        "#FFFFFF", "#B0B0B0", "#404040", "#000000",
    };

    /// <summary>Raised whenever the colour changes, including while dragging a slider.</summary>
    public event Action<Rgb24>? ColorChanged;

    public Rgb24 Color
    {
        get => _color;
        set
        {
            _color = value;
            _swatch.Background = new SolidColorBrush(value.ToMediaColor());
        }
    }

    public ColorPickerButton(string caption, Rgb24 initial)
    {
        Margin = new Thickness(0, 0, 12, 12);

        _swatch = new Border
        {
            Width = 76,
            Height = 46,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(initial.ToMediaColor()),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        _swatch.MouseLeftButtonUp += (_, _) => Open();

        _caption = new TextBlock
        {
            Text = caption,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
        };

        Children.Add(_swatch);
        Children.Add(_caption);

        _color = initial;
        _popup = BuildPopup();
    }

    private Popup BuildPopup()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = _caption.Text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });

        _preview = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(_color.ToMediaColor()),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        panel.Children.Add(_preview);

        // ---- hue ----
        panel.Children.Add(SectionLabel("HUE"));
        var hueTrack = new Border { Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        var hueGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
            hueGradient.GradientStops.Add(new GradientStop(RgbF.FromHsv(i / 6.0, 1, 1).ToRgb24().ToMediaColor(), i / 6.0));
        hueTrack.Background = hueGradient;
        _hue = MakeSlider();
        panel.Children.Add(Layer(hueTrack, _hue));

        // ---- saturation ----
        panel.Children.Add(SectionLabel("SATURATION"));
        _satTrack = new Border { Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        _sat = MakeSlider();
        panel.Children.Add(Layer(_satTrack, _sat));

        // ---- brightness ----
        panel.Children.Add(SectionLabel("BRIGHTNESS"));
        _valTrack = new Border { Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        _val = MakeSlider();
        panel.Children.Add(Layer(_valTrack, _val));

        // ---- presets ----
        panel.Children.Add(SectionLabel("PRESETS"));
        var wrap = new WrapPanel();
        foreach (string hex in Presets)
        {
            var c = Rgb24.FromHex(hex);
            var chip = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c.ToMediaColor()),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            chip.MouseLeftButtonUp += (_, _) => { LoadColor(c); Commit(c); };
            wrap.Children.Add(chip);
        }
        panel.Children.Add(wrap);

        // ---- hex + done ----
        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _hex = new TextBox { FontFamily = new FontFamily("Consolas"), Text = _color.ToHex() };
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
        _hex.LostFocus += (_, _) => CommitHex();
        Grid.SetColumn(_hex, 0);
        bottom.Children.Add(_hex);

        var done = new Button { Content = "Done", Margin = new Thickness(10, 0, 0, 0) };
        if (Application.Current.TryFindResource("StandardButton") is Style s) done.Style = s;
        done.Click += (_, _) => _popup.IsOpen = false;
        Grid.SetColumn(done, 1);
        bottom.Children.Add(done);

        panel.Children.Add(bottom);

        var shell = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2C)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Width = 296,
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 5,
                Opacity = 0.55,
                Color = Colors.Black,
            },
        };

        return new Popup
        {
            Child = shell,
            Placement = PlacementMode.Bottom,
            PlacementTarget = _swatch,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -8,
            VerticalOffset = 6,
        };
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 6),
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
    };

    private Slider MakeSlider()
    {
        var s = new Slider { Minimum = 0, Maximum = 1 };
        if (Application.Current.TryFindResource("OverlaySlider") is Style style) s.Style = style;
        s.ValueChanged += (_, _) => OnHsvChanged();
        return s;
    }

    private static Grid Layer(UIElement track, UIElement slider)
    {
        var g = new Grid { Height = 20 };
        g.Children.Add(track);
        g.Children.Add(slider);
        return g;
    }

    private void Open()
    {
        LoadColor(_color);
        _popup.IsOpen = true;
    }

    private void LoadColor(Rgb24 c)
    {
        _updating = true;
        var (h, s, v) = RgbF.From(c).ToHsv();
        _hue.Value = h;
        _sat.Value = s;
        _val.Value = v;
        _hex.Text = c.ToHex();
        _updating = false;
        UpdateTracks(c);
    }

    private void OnHsvChanged()
    {
        if (_updating) return;
        var c = RgbF.FromHsv(_hue.Value, _sat.Value, _val.Value).ToRgb24();

        _updating = true;
        _hex.Text = c.ToHex();
        _updating = false;

        Commit(c);
    }

    private void CommitHex()
    {
        if (_updating) return;
        var c = Rgb24.FromHex(_hex.Text);
        LoadColor(c);
        Commit(c);
    }

    private void Commit(Rgb24 c)
    {
        Color = c;
        UpdateTracks(c);
        ColorChanged?.Invoke(c);
    }

    private void UpdateTracks(Rgb24 c)
    {
        _preview.Background = new SolidColorBrush(c.ToMediaColor());

        double v = _val.Value <= 0.05 ? 1.0 : _val.Value;
        var grey = RgbF.FromHsv(_hue.Value, 0, v).ToRgb24();
        var pure = RgbF.FromHsv(_hue.Value, 1, v).ToRgb24();
        _satTrack.Background = new LinearGradientBrush(grey.ToMediaColor(), pure.ToMediaColor(), 0);

        var full = RgbF.FromHsv(_hue.Value, _sat.Value, 1).ToRgb24();
        _valTrack.Background = new LinearGradientBrush(Colors.Black, full.ToMediaColor(), 0);
    }
}
