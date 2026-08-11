using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SickRGB.Audio;
using SickRGB.Capture;
using SickRGB.Controls;
using SickRGB.Core;
using SickRGB.Effects;

namespace SickRGB.Views;

public partial class EffectsPage : UserControl, IRefreshablePage
{
    private readonly AppServices _services = AppServices.Current;
    private readonly List<Effect> _effects = EffectLibrary.CreateAll().ToList();
    private bool _loading = true;

    public EffectsPage()
    {
        InitializeComponent();

        BuildGallery();
        BuildMonitorList();

        _loading = true;
        BrightnessSlider.Value = _services.Settings.Brightness;
        SaturationSlider.Value = _services.Settings.Saturation;
        SmoothingSlider.Value = _services.Settings.Smoothing;
        FloorSlider.Value = _services.Settings.AmbientFloor;
        ChkCanvasMap.IsChecked = _services.Settings.AmbientUseCanvasMapping;

        BuildAudioChoices();
        ChkMicrophone.IsChecked = _services.Settings.AudioUseMicrophone;
        GainSlider.Value = _services.Settings.AudioGain;
        AudioSmoothSlider.Value = _services.Settings.AudioSmoothing;
        GateSlider.Value = _services.Settings.AudioNoiseGate;
        MinHzSlider.Value = _services.Settings.AudioMinHz;
        MaxHzSlider.Value = _services.Settings.AudioMaxHz;
        AudioFloorSlider.Value = _services.Settings.AudioFloor;
        _loading = false;

        UpdateValueLabels();
        SelectEffect(_services.Settings.GlobalEffectId);
    }

    public void OnShown() => BuildMonitorList();

    // ================================================================== gallery

    private void BuildGallery()
    {
        EffectGallery.Items.Clear();

        foreach (var effect in _effects)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = effect.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
            });

            var tags = new List<string>();
            if (effect.IsReactive) tags.Add("reacts to you");
            if (effect.UsesScreen) tags.Add("follows your screen");

            panel.Children.Add(new TextBlock
            {
                Text = tags.Count > 0 ? string.Join("  ·  ", tags) : "always on",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
            });

            EffectGallery.Items.Add(new ListBoxItem { Content = panel, Tag = effect.Id });
        }
    }

    private void SelectEffect(string id)
    {
        for (int i = 0; i < EffectGallery.Items.Count; i++)
        {
            if (EffectGallery.Items[i] is ListBoxItem item && (string?)item.Tag == id)
            {
                EffectGallery.SelectedIndex = i;
                return;
            }
        }
        if (EffectGallery.Items.Count > 0) EffectGallery.SelectedIndex = 0;
    }

    private void EffectGallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectGallery.SelectedItem is not ListBoxItem item || item.Tag is not string id) return;

        var effect = _effects.FirstOrDefault(x => x.Id == id) ?? _effects[0];

        _services.Settings.GlobalEffectId = id;
        _services.Settings.Save();
        _services.Engine.Invalidate();

        EffectTitle.Text = effect.Name;
        EffectDesc.Text = effect.Description;
        ReactiveNote.Visibility = effect.IsReactive ? Visibility.Visible : Visibility.Collapsed;

        var preset = _services.Settings.PresetFor(id);

        _loading = true;
        SpeedSlider.Value = preset.Speed;
        IntensitySlider.Value = preset.Intensity;
        _loading = false;

        IntensityLabel.Text = effect.IntensityLabel;
        SpeedSection.Visibility = effect.UsesSpeed ? Visibility.Visible : Visibility.Collapsed;
        IntensitySection.Visibility = effect.UsesIntensity ? Visibility.Visible : Visibility.Collapsed;
        AmbientSection.Visibility = effect.UsesScreen ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = effect.UsesAudio ? Visibility.Visible : Visibility.Collapsed;
        if (effect.UsesAudio) UpdateAudioStatus();

        _currentEffect = effect;
        RefreshColourSection();
        UpdateValueLabels();
    }

    private Effect? _currentEffect;

    /// <summary>
    /// Which colour swatches to show.
    ///
    /// The visualiser is the one effect where this depends on another setting: a rainbow
    /// or a level meter picks its own colours, so showing five empty swatches beside them
    /// would just be clutter.
    /// </summary>
    private string[] ColourLabelsFor(Effect effect)
    {
        if (effect is not AudioVisualizerEffect) return effect.ColorLabels;

        return _services.Settings.AudioColourMode switch
        {
            AudioColourMode.Palette => effect.ColorLabels,
            AudioColourMode.Single => new[] { "Colour" },
            _ => Array.Empty<string>(),
        };
    }

    private void RefreshColourSection()
    {
        if (_currentEffect is null) return;

        var labels = ColourLabelsFor(_currentEffect);
        ColorSection.Visibility = labels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildColorPickers(_currentEffect, _currentEffect.Id, labels);
    }

    private void BuildColorPickers(Effect effect, string effectId, string[] labels)
    {
        ColorPanel.Children.Clear();
        var preset = _services.Settings.PresetFor(effectId);

        for (int i = 0; i < labels.Length; i++)
        {
            int slot = i;
            var initial = Rgb24.FromHex(i < preset.Colors.Length ? preset.Colors[i] : "#000000");

            var picker = new ColorPickerButton(labels[i], initial);
            picker.ColorChanged += c =>
            {
                if (slot < preset.Colors.Length)
                {
                    preset.Colors[slot] = c.ToHex();
                    _services.Settings.Save();
                }
            };
            ColorPanel.Children.Add(picker);
        }
    }

    // ================================================================== monitors

    private void BuildMonitorList()
    {
        var previous = _services.Settings.CaptureTargetName;

        _loading = true;
        MonitorList.Items.Clear();
        var targets = ScreenSampler.EnumerateTargets();
        foreach (var t in targets) MonitorList.Items.Add(t);

        int index = targets.FindIndex(t => t.Name == previous);
        MonitorList.SelectedIndex = index >= 0 ? index : 0;
        _loading = false;
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MonitorList.SelectedItem is CaptureTarget t)
        {
            _services.Settings.CaptureTargetName = t.Name;
            _services.Settings.Save();
        }
    }

    // ================================================================== sliders

    private void UpdateValueLabels()
    {
        SpeedValue.Text = $"{SpeedSlider.Value * 100:0}%";
        IntensityValue.Text = $"{IntensitySlider.Value * 100:0}%";
        BrightnessValue.Text = $"{BrightnessSlider.Value * 100:0}%";
        SaturationValue.Text = $"{SaturationSlider.Value:0.00}x";
        SmoothingValue.Text = $"{SmoothingSlider.Value * 100:0}%";
        FloorValue.Text = $"{FloorSlider.Value * 100:0}%";
    }

    private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.PresetFor(_services.Settings.GlobalEffectId).Speed = e.NewValue;
        _services.Settings.Save();
        SpeedValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void Intensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.PresetFor(_services.Settings.GlobalEffectId).Intensity = e.NewValue;
        _services.Settings.Save();
        IntensityValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.Brightness = e.NewValue;
        _services.Settings.Save();
        BrightnessValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void Saturation_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.Saturation = e.NewValue;
        _services.Settings.Save();
        SaturationValue.Text = $"{e.NewValue:0.00}x";
    }

    private void Smoothing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.Smoothing = e.NewValue;
        _services.Settings.Save();
        SmoothingValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void Floor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _services.Settings.AmbientFloor = e.NewValue;
        _services.Settings.Save();
        FloorValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void CanvasMap_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.AmbientUseCanvasMapping = ChkCanvasMap.IsChecked == true;
        _services.Settings.Save();
    }

    // ================================================================== audio

    private static readonly (AudioColourMode Mode, string Label)[] ColourModes =
    {
        (AudioColourMode.Spectrum, "Rainbow across the frequencies"),
        (AudioColourMode.Palette,  "My own colour per frequency"),
        (AudioColourMode.Single,   "One colour"),
        (AudioColourMode.Meter,    "Green to red, like a level meter"),
    };

    private static readonly (AudioLayout Layout, string Label)[] Layouts =
    {
        (AudioLayout.LeftToRight,  "On the left"),
        (AudioLayout.RightToLeft,  "On the right"),
        (AudioLayout.BassInCentre, "In the middle, spreading out"),
        (AudioLayout.BassAtEdges,  "At both edges, treble centre"),
    };

    private void BuildAudioChoices()
    {
        CmbAudioColour.Items.Clear();
        foreach (var (_, label) in ColourModes) CmbAudioColour.Items.Add(label);
        CmbAudioColour.SelectedIndex = Math.Max(0,
            Array.FindIndex(ColourModes, c => c.Mode == _services.Settings.AudioColourMode));

        CmbAudioLayout.Items.Clear();
        foreach (var (_, label) in Layouts) CmbAudioLayout.Items.Add(label);
        CmbAudioLayout.SelectedIndex = Math.Max(0,
            Array.FindIndex(Layouts, l => l.Layout == _services.Settings.AudioLayout));
    }

    private void UpdateAudioStatus()
    {
        string? error = _services.Engine.AudioError;

        if (!string.IsNullOrEmpty(error))
        {
            AudioStatus.Text = $"Could not listen to audio: {error}";
            AudioStatus.Foreground = (Brush)FindResource("WarningBrush");
            return;
        }

        AudioStatus.Text = _services.Settings.AudioUseMicrophone
            ? "Listening to your microphone."
            : "Listening to whatever your PC is playing. Start some music to see it move.";
        AudioStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");
    }

    private void Microphone_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.AudioUseMicrophone = ChkMicrophone.IsChecked == true;
        _services.Settings.Save();
        UpdateAudioStatus();
    }

    private void AudioColour_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = CmbAudioColour.SelectedIndex;
        if (i < 0 || i >= ColourModes.Length) return;
        _services.Settings.AudioColourMode = ColourModes[i].Mode;
        _services.Settings.Save();

        // Which swatches make sense depends on the mode just chosen.
        RefreshColourSection();
    }

    private void AudioLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = CmbAudioLayout.SelectedIndex;
        if (i < 0 || i >= Layouts.Length) return;
        _services.Settings.AudioLayout = Layouts[i].Layout;
        _services.Settings.Save();
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GainValue.Text = $"{e.NewValue:0.0}x";
        if (_loading) return;
        _services.Settings.AudioGain = e.NewValue;
        _services.Settings.Save();
    }

    private void AudioSmooth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AudioSmoothValue.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        _services.Settings.AudioSmoothing = e.NewValue;
        _services.Settings.Save();
    }

    private void Gate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GateValue.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        _services.Settings.AudioNoiseGate = e.NewValue;
        _services.Settings.Save();
    }

    private void MinHz_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MinHzValue.Text = $"{e.NewValue:0} Hz";
        if (_loading) return;
        _services.Settings.AudioMinHz = e.NewValue;
        _services.Settings.Save();
    }

    private void MaxHz_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MaxHzValue.Text = $"{e.NewValue / 1000.0:0.0} kHz";
        if (_loading) return;
        _services.Settings.AudioMaxHz = e.NewValue;
        _services.Settings.Save();
    }

    private void AudioFloor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AudioFloorValue.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        _services.Settings.AudioFloor = e.NewValue;
        _services.Settings.Save();
    }
}
