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
        ChkBalance.IsChecked = _services.Settings.AudioBalanceCompensation;
        BalanceTrimSlider.Value = _services.Settings.AudioBalanceTrim;
        ChkDirectionOverlay.IsChecked = _services.Settings.DirectionOverlay;
        OverlayOpacitySlider.Value = _services.Settings.DirectionOverlayOpacity;
        _loading = false;

        UpdateValueLabels();
        SelectEffect(_services.Settings.GlobalEffectId);

        _balanceTicker.Tick += (_, _) =>
        {
            if (AudioSection.Visibility == Visibility.Visible) UpdateBalanceLabels();
        };
    }

    public void OnShown()
    {
        BuildMonitorList();
        _balanceTicker.Start();
    }

    /// <summary>
    /// Keeps the measured balance readout current.
    ///
    /// The measurement moves over tens of seconds by design, so a slow tick is plenty and
    /// there is nothing to gain from tying it to the render loop.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _balanceTicker = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

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

        // The overlay is offered on anything except the directional effect itself, where
        // it would be layering the effect over a copy of itself.
        bool isDirection = effect is DirectionalSoundEffect;
        OverlaySection.Visibility = isDirection ? Visibility.Collapsed : Visibility.Visible;
        OverlayOpacityRow.Visibility =
            !isDirection && _services.Settings.DirectionOverlay ? Visibility.Visible : Visibility.Collapsed;

        // The sound settings also apply to the overlay, so they stay available whenever
        // anything is listening rather than only when the effect itself is.
        bool overlayOn = !isDirection && _services.Settings.DirectionOverlay;
        AudioSection.Visibility = effect.UsesAudio || overlayOn ? Visibility.Visible : Visibility.Collapsed;

        // Both audio effects share sensitivity, smoothing and the gate, but frequency
        // layout is meaningless for a directional readout.
        var visualiserOnly = effect is AudioVisualizerEffect ? Visibility.Visible : Visibility.Collapsed;
        VisualiserModeRow.Visibility = visualiserOnly;
        VisualiserRangeRow.Visibility = visualiserOnly;

        if (effect.UsesAudio || overlayOn)
        {
            UpdateAudioStatus();
            RefreshAudioSources();
        }

        _currentEffect = effect;
        RefreshColourSection();
        UpdateValueLabels();
    }

    private Effect? _currentEffect;

    /// <summary>
    /// Swatch labels. The visualiser's stops are always shown, but what they are laid out
    /// along depends on the mode, so they are named for it.
    /// </summary>
    private string[] ColourLabelsFor(Effect effect)
    {
        if (effect is not AudioVisualizerEffect) return effect.ColorLabels;

        return _services.Settings.AudioColourMode == AudioColourMode.Meter
            ? new[] { "Silent", "Quiet", "Medium", "Loud", "Peak" }
            : effect.ColorLabels;
    }

    /// <summary>The colours a preset mode fills the stops with.</summary>
    private static string[]? PresetColoursFor(AudioColourMode mode, string currentFirst) => mode switch
    {
        AudioColourMode.Spectrum => new[] { "#FF2D3C", "#FF9114", "#3CE66E", "#00C8FF", "#DC46FF" },
        AudioColourMode.Meter => new[] { "#1FBF5A", "#6CD432", "#FFD400", "#FF8A14", "#FF2D2D" },
        AudioColourMode.Single => new[] { currentFirst, currentFirst, currentFirst, currentFirst, currentFirst },

        // "My own colours" is the one mode that leaves what is there alone.
        _ => null,
    };

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
            bool isAudio = effect is AudioVisualizerEffect;
            picker.ColorChanged += c =>
            {
                if (slot < preset.Colors.Length)
                {
                    preset.Colors[slot] = c.ToHex();
                    _services.Settings.Save();
                }

                if (isAudio) TakeOwnershipOfColours();
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
        OverlayOpacityValue.Text = $"{OverlayOpacitySlider.Value * 100:0}%";
        UpdateBalanceLabels();
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

    /// <summary>
    /// Only speaks up when something is wrong. When it is working there is nothing worth
    /// saying: the lights are the feedback.
    /// </summary>
    private void UpdateAudioStatus()
    {
        string? error = _services.Engine.AudioError;

        if (string.IsNullOrEmpty(error))
        {
            AudioStatus.Visibility = Visibility.Collapsed;
            return;
        }

        AudioStatus.Text = $"Could not listen to audio: {error}";
        AudioStatus.Visibility = Visibility.Visible;
    }

    private void Microphone_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.AudioUseMicrophone = ChkMicrophone.IsChecked == true;
        _services.Settings.Save();

        // Choosing one app applies to loopback only, so the picker goes away for a mic.
        AudioSourceRow.Visibility = _services.Settings.AudioUseMicrophone
            ? Visibility.Collapsed : Visibility.Visible;

        UpdateAudioStatus();
    }

    // ================================================================== audio source

    /// <summary>Backs the picker, index-aligned with it. The first entry is "everything".</summary>
    private readonly List<AudioSession?> _audioSources = new();

    /// <summary>
    /// Refills the app picker from whatever currently holds an audio session.
    ///
    /// Rebuilt each time the panel is shown rather than kept live: applications come and
    /// go constantly, and a list that reshuffled itself under the pointer would be worse
    /// than one that is a few seconds stale.
    /// </summary>
    private void RefreshAudioSources()
    {
        bool wasLoading = _loading;
        _loading = true;

        try
        {
            _audioSources.Clear();
            CmbAudioSource.Items.Clear();

            _audioSources.Add(null);
            CmbAudioSource.Items.Add("Everything playing on this PC");

            string saved = _services.Settings.AudioTargetProcessName;
            int selected = 0;
            bool savedFound = false;

            foreach (var session in AudioSessions.List())
            {
                _audioSources.Add(session);
                CmbAudioSource.Items.Add(session.Active
                    ? $"{session.DisplayName}  (playing)"
                    : session.DisplayName);

                if (!savedFound && string.Equals(session.ProcessName, saved, StringComparison.OrdinalIgnoreCase))
                {
                    selected = CmbAudioSource.Items.Count - 1;
                    savedFound = true;
                }
            }

            // A chosen app that is not running stays chosen: it is almost always a game
            // that is simply closed, and silently reverting to everything would be a
            // change nobody asked for.
            if (!savedFound && !string.IsNullOrWhiteSpace(saved))
            {
                _audioSources.Add(new AudioSession { ProcessName = saved, DisplayName = saved });
                CmbAudioSource.Items.Add($"{saved}  (not running)");
                selected = CmbAudioSource.Items.Count - 1;
            }

            CmbAudioSource.SelectedIndex = selected;

            AudioSourceWarning.Visibility = _services.Engine.AudioTargetMissing
                ? Visibility.Visible : Visibility.Collapsed;
            AudioSourceWarning.Text =
                $"{saved} is not playing anything right now, so there is nothing to show. It will be picked up as soon as it is.";

            AudioSourceRow.Visibility = _services.Settings.AudioUseMicrophone
                ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void RefreshAudioSources_Click(object sender, RoutedEventArgs e) => RefreshAudioSources();

    private void AudioSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int i = CmbAudioSource.SelectedIndex;
        if (i < 0 || i >= _audioSources.Count) return;

        var session = _audioSources[i];
        _services.Settings.AudioTargetProcessId = session?.ProcessId;
        _services.Settings.AudioTargetProcessName = session?.ProcessName ?? "";
        _services.Settings.Save();

        AudioSourceWarning.Visibility = Visibility.Collapsed;
    }

    // ================================================================== balance

    private void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.AudioBalanceCompensation = ChkBalance.IsChecked == true;
        _services.Settings.Save();
        UpdateValueLabels();
    }

    private void BalanceTrim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBalanceLabels();
        if (_loading) return;
        _services.Settings.AudioBalanceTrim = e.NewValue;
        _services.Settings.Save();
    }

    private void UpdateBalanceLabels()
    {
        // Decibels throughout, because that is the unit anything else that measures a
        // channel balance reports, and because the differences involved are far too large
        // for a percentage to describe usefully.
        double trimDb = BalanceTrimSlider.Value * DirectionAnalyzer.TrimRangeDb / 2.0;

        BalanceTrimValue.Text = Math.Abs(trimDb) < 0.5
            ? "Centred"
            : trimDb > 0 ? $"{trimDb:0} dB towards the right" : $"{-trimDb:0} dB towards the left";

        if (!_services.Engine.AudioBalanceSettled)
        {
            BalanceReadout.Text = "Listening. Play something for a few seconds and the measurement will appear here.";
            return;
        }

        double db = _services.Engine.MeasuredAudioImbalanceDb;
        string side = db > 0 ? "right" : "left";
        string tail = ChkBalance.IsChecked == true ? ", and correcting for it" : "";

        BalanceReadout.Text = Math.Abs(db) < 1.0
            ? "Measuring your two channels as about even."
            : $"Measuring your {side} channel about {Math.Abs(db):0} dB louder than the other{tail}.";
    }

    private void ResetBalance_Click(object sender, RoutedEventArgs e)
    {
        _services.Engine.ResetAudioBalance();
        UpdateBalanceLabels();
    }

    // ================================================================== overlay

    private void DirectionOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool on = ChkDirectionOverlay.IsChecked == true;
        _services.Settings.DirectionOverlay = on;
        _services.Settings.Save();

        OverlayOpacityRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // Turning it on brings the sound settings into play for an effect that had none.
        bool usesAudio = _currentEffect?.UsesAudio == true;
        AudioSection.Visibility = usesAudio || on ? Visibility.Visible : Visibility.Collapsed;

        if (on) RefreshAudioSources();
    }

    private void OverlayOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OverlayOpacityValue.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        _services.Settings.DirectionOverlayOpacity = e.NewValue;
        _services.Settings.Save();
    }

    private void AudioColour_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = CmbAudioColour.SelectedIndex;
        if (i < 0 || i >= ColourModes.Length) return;
        var mode = ColourModes[i].Mode;
        _services.Settings.AudioColourMode = mode;

        // Picking a preset rewrites the five stops, so the swatches always show the
        // colours actually in use rather than something left over from before.
        var preset = _services.Settings.PresetFor("audio");
        string first = preset.Colors.Length > 0 ? preset.Colors[0] : "#FF5A1F";
        if (PresetColoursFor(mode, first) is { } colours) preset.Colors = colours;

        _services.Settings.Save();
        RefreshColourSection();
    }

    /// <summary>
    /// Editing a swatch means the colours are now yours, so the mode follows along.
    ///
    /// The pickers are deliberately not rebuilt here: doing so would tear down the very
    /// control being used mid-edit. The stop names catch up next time the page is opened.
    /// </summary>
    private void TakeOwnershipOfColours()
    {
        if (_services.Settings.AudioColourMode == AudioColourMode.Palette) return;

        _services.Settings.AudioColourMode = AudioColourMode.Palette;
        _services.Settings.Save();

        // Move the dropdown without letting it overwrite the colour just chosen.
        _loading = true;
        CmbAudioColour.SelectedIndex = Array.FindIndex(ColourModes, c => c.Mode == AudioColourMode.Palette);
        _loading = false;
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
