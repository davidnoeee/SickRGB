using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SickRGB.Core;
using SickRGB.Devices;
using SickRGB.Effects;

namespace SickRGB.Views;

public partial class DevicesPage : UserControl, IRefreshablePage
{
    private readonly AppServices _services = AppServices.Current;
    private readonly List<(LightDevice Device, Border[] Cells)> _previews = new();
    private bool _building;

    public DevicesPage()
    {
        InitializeComponent();
        // Re-subscribe on every load. The shell caches page instances, so subscribing
        // only in the constructor would leave the preview strips frozen after the first
        // time you navigate away from this page.
        Loaded += (_, _) =>
        {
            _services.Engine.FrameRendered -= OnFrameRendered;   // guard against double-subscribing
            _services.Engine.FrameRendered += OnFrameRendered;
            OnShown();
        };
        Unloaded += (_, _) => _services.Engine.FrameRendered -= OnFrameRendered;
    }

    public void OnShown()
    {
        BuildProviders();
        BuildDevices();
    }

    // ================================================================== providers

    private void BuildProviders()
    {
        ProviderPanel.Children.Clear();

        foreach (var provider in _services.Registry.Providers)
        {
            int deviceCount = _services.Registry.Devices.Count(d => d.ProviderId == provider.Id);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 12, 0),
                Fill = (Brush)FindResource(provider.IsAvailable ? "SuccessBrush" : "TextTertiaryBrush"),
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = provider.DisplayName, Style = (Style)FindResource("StrongText") });
            text.Children.Add(new TextBlock
            {
                Text = provider.Description,
                Style = (Style)FindResource("CaptionText"),
                Margin = new Thickness(0, 3, 0, 0),
            });

            if (!provider.IsAvailable && !string.IsNullOrEmpty(provider.UnavailableReason))
            {
                text.Children.Add(new TextBlock
                {
                    Text = provider.UnavailableReason,
                    Style = (Style)FindResource("CaptionText"),
                    Foreground = (Brush)FindResource("WarningBrush"),
                    Margin = new Thickness(0, 5, 0, 0),
                });

                // Offer the guided setup right where the problem is reported.
                if (provider is Devices.Providers.OpenRgbProvider)
                {
                    var setup = new Button
                    {
                        Content = "Set up OpenRGB for me",
                        Style = (Style)FindResource("StandardButton"),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 10, 0, 0),
                    };
                    setup.Click += (_, _) =>
                    {
                        var dialog = new OpenRgbSetupWindow { Owner = Window.GetWindow(this) };
                        dialog.ShowDialog();
                        OnShown();
                    };
                    text.Children.Add(setup);
                }
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var badge = new Border
            {
                Background = (Brush)FindResource("ControlBrush"),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(11, 4, 11, 4),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = deviceCount == 1 ? "1 device" : $"{deviceCount} devices",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                },
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);

            ProviderPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("SubtleCard"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(16, 13, 16, 13),
                Child = grid,
            });
        }
    }

    // ================================================================== devices

    private void BuildDevices()
    {
        _building = true;
        DevicePanel.Children.Clear();
        _previews.Clear();

        var devices = _services.Registry.Devices;
        EmptyCard.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var device in devices)
            DevicePanel.Children.Add(BuildDeviceCard(device));

        _building = false;
    }

    private Border BuildDeviceCard(LightDevice device)
    {
        var settings = _services.Settings.DeviceFor(device.Key);

        var root = new StackPanel();

        // ---- header row: name + enable toggle ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = device.Name,
            Style = (Style)FindResource("SubtitleText"),
            FontSize = 16,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = device.Details,
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var enableToggle = new CheckBox
        {
            Style = (Style)FindResource("ToggleSwitch"),
            IsChecked = device.Enabled,
            VerticalAlignment = VerticalAlignment.Top,
        };
        enableToggle.Click += (_, _) =>
        {
            device.Enabled = enableToggle.IsChecked == true;
            settings.Enabled = device.Enabled;
            _services.Settings.Save();
            _services.Engine.LayoutChanged();
        };
        Grid.SetColumn(enableToggle, 1);
        header.Children.Add(enableToggle);

        root.Children.Add(header);

        // ---- live preview strip ----
        var preview = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 14, 0, 0),
            ClipToBounds = true,
        };

        // Cap how many cells are drawn: a 100-LED strip does not need 100 preview slivers.
        int cellCount = Math.Min(device.ZoneCount, 48);
        var grid = new UniformGrid { Rows = 1, Columns = cellCount };
        var cells = new Border[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            cells[i] = new Border { Background = Brushes.Black };
            grid.Children.Add(cells[i]);
        }
        preview.Child = grid;
        root.Children.Add(preview);
        _previews.Add((device, cells));

        // ---- controls row ----
        var controls = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };

        controls.Children.Add(LabelledControl("Role", BuildRoleCombo(device, settings), 168));
        controls.Children.Add(LabelledControl("Lighting", BuildEffectCombo(device, settings), 210));
        controls.Children.Add(LabelledControl("Update rate", BuildRateCombo(device, settings), 196));

        var flip = new CheckBox
        {
            Style = (Style)FindResource("ToggleSwitch"),
            Content = "Mirror",
            IsChecked = device.Reversed,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 18, 0),
            ToolTip = "Reverses the order of this device's lights, for hardware fitted the other way round.",
        };
        flip.Click += (_, _) =>
        {
            device.Reversed = flip.IsChecked == true;
            settings.Reversed = device.Reversed;
            _services.Settings.Save();
            _services.Engine.LayoutChanged();
        };
        controls.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { flip },
        });

        root.Children.Add(controls);

        // Addressable headers need their strip length entered by hand.
        if (device.ResizableHeaders.Count > 0)
            root.Children.Add(BuildHeaderSizing(device));

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = root,
        };
    }

    /// <summary>
    /// Builds the "how long is the strip" editor for addressable headers.
    ///
    /// Motherboard ARGB headers and fan hubs cannot measure what is plugged into them, so
    /// they report zero LEDs and stay dark until told. This is where a case strip gets
    /// switched on.
    /// </summary>
    private Border BuildHeaderSizing(LightDevice device)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "Addressable headers",
            Style = (Style)FindResource("StrongText"),
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "These sockets can't tell how many LEDs are plugged into them, so nothing lights up until "
                 + "you say. Count the LEDs on the strip or fan ring and enter it here.",
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(0, 5, 0, 10),
        });

        foreach (var header in device.ResizableHeaders)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            row.Children.Add(new TextBlock
            {
                Text = header.Name,
                Width = 190,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 13,
            });

            var box = new TextBox
            {
                Text = header.CurrentLeds.ToString(),
                Width = 84,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(box);

            row.Children.Add(new TextBlock
            {
                Text = $"LEDs  (up to {header.MaxLeds})",
                Margin = new Thickness(10, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("CaptionText"),
            });

            var apply = new Button
            {
                Content = "Apply",
                Style = (Style)FindResource("StandardButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var captured = header;
            apply.Click += async (_, _) =>
            {
                if (!int.TryParse(box.Text.Trim(), out int count)) return;
                count = Math.Clamp(count, captured.MinLeds, captured.MaxLeds);
                box.Text = count.ToString();

                apply.IsEnabled = false;
                apply.Content = "Applying...";

                var provider = _services.Registry.GetProvider<Devices.Providers.OpenRgbProvider>();
                bool ok = provider is not null &&
                          await provider.ResizeHeaderAsync(device, captured.ZoneIndex, count);

                if (ok)
                {
                    // Re-reading rebuilds this whole page, so these controls are gone
                    // after this point and must not be touched again.
                    await _services.Engine.RescanAsync();
                    return;
                }

                apply.Content = "Failed";
                apply.IsEnabled = true;
            };
            row.Children.Add(apply);

            panel.Children.Add(row);
        }

        return new Border
        {
            Style = (Style)FindResource("SubtleCard"),
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(14, 12, 14, 8),
            Child = panel,
        };
    }

    private static StackPanel LabelledControl(string label, UIElement control, double width)
    {
        var panel = new StackPanel { Width = width, Margin = new Thickness(0, 0, 16, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
            Foreground = new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
        });
        panel.Children.Add(control);
        return panel;
    }

    private ComboBox BuildRoleCombo(LightDevice device, DeviceSettings settings)
    {
        var combo = new ComboBox();
        foreach (DeviceRole role in Enum.GetValues<DeviceRole>())
            combo.Items.Add(role);
        combo.SelectedItem = device.Role;

        combo.SelectionChanged += (_, _) =>
        {
            if (_building || combo.SelectedItem is not DeviceRole role) return;
            device.Role = role;
            settings.Role = role;
            _services.Settings.Save();
            _services.Engine.Invalidate();
        };

        combo.ToolTip = "Decides where key presses and clicks start from on your layout. " +
                        "Make sure your keyboard and mouse are set correctly.";
        return combo;
    }

    /// <summary>
    /// Rates offered per device. null = follow the provider's suggestion, 0 = no limit.
    /// The slow end matters: memory on SMBus tops out around 8 per second, and sending
    /// faster only builds a backlog.
    /// </summary>
    private static readonly int?[] RateOptions = { null, 0, 60, 30, 20, 15, 10, 8, 5, 2 };

    private ComboBox BuildRateCombo(LightDevice device, DeviceSettings settings)
    {
        var combo = new ComboBox();

        foreach (int? option in RateOptions)
        {
            combo.Items.Add(option switch
            {
                null when device.DefaultMaxUpdatesPerSecond > 0
                    => $"Automatic ({device.DefaultMaxUpdatesPerSecond} per second)",
                null => "Automatic (no limit)",
                0 => "As fast as possible",
                _ => $"{option} per second",
            });
        }

        int index = Array.FindIndex(RateOptions, o => o == settings.UpdateRate);
        combo.SelectedIndex = index >= 0 ? index : 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            int i = combo.SelectedIndex;
            if (i < 0 || i >= RateOptions.Length) return;

            settings.UpdateRate = RateOptions[i];
            device.MaxUpdatesPerSecond = RateOptions[i] ?? device.DefaultMaxUpdatesPerSecond;

            _services.Settings.Save();
            _services.Engine.Invalidate();
        };

        combo.ToolTip = "How often this device is sent new colours. Lower it for hardware on a slow "
                      + "connection - sending faster than it can keep up only makes it lag behind.";
        return combo;
    }

    private ComboBox BuildEffectCombo(LightDevice device, DeviceSettings settings)
    {
        var combo = new ComboBox();
        combo.Items.Add("Follow the main effect");
        foreach (var effect in EffectLibrary.CreateAll())
            combo.Items.Add(effect.Name);

        var all = EffectLibrary.CreateAll();
        combo.SelectedIndex = settings.SyncToGlobal
            ? 0
            : Math.Max(0, all.ToList().FindIndex(e => e.Id == settings.EffectId) + 1);

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            int index = combo.SelectedIndex;

            if (index <= 0)
            {
                settings.SyncToGlobal = true;
            }
            else
            {
                settings.SyncToGlobal = false;
                settings.EffectId = all[index - 1].Id;
            }

            _services.Settings.Save();
            _services.Engine.Invalidate();
        };

        combo.ToolTip = "Give this device an effect of its own, or let it follow the one on the Effects page.";
        return combo;
    }

    // ================================================================== live preview

    private void OnFrameRendered()
    {
        if (!IsVisible) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            foreach (var (device, cells) in _previews)
            {
                if (cells.Length == 0) continue;

                for (int i = 0; i < cells.Length; i++)
                {
                    // When capped, sample evenly across the device's real lights.
                    int zoneIndex = cells.Length == device.ZoneCount
                        ? i
                        : (int)((long)i * device.ZoneCount / cells.Length);

                    if (zoneIndex >= device.Zones.Count) continue;

                    var c = device.Enabled ? device.Zones[zoneIndex].Current : Rgb24.Black;
                    cells[i].Background = new SolidColorBrush(c.ToMediaColor());
                }
            }
        });
    }
}
