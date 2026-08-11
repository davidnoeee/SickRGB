using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        ColorSection.Visibility = effect.ColorLabels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AmbientSection.Visibility = effect.UsesScreen ? Visibility.Visible : Visibility.Collapsed;

        BuildColorPickers(effect, id);
        UpdateValueLabels();
    }

    private void BuildColorPickers(Effect effect, string effectId)
    {
        ColorPanel.Children.Clear();
        var preset = _services.Settings.PresetFor(effectId);

        for (int i = 0; i < effect.ColorLabels.Length; i++)
        {
            int slot = i;
            var initial = Rgb24.FromHex(i < preset.Colors.Length ? preset.Colors[i] : "#000000");

            var picker = new ColorPickerButton(effect.ColorLabels[i], initial);
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
}
