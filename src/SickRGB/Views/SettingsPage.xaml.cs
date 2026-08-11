using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SickRGB.Core;
using SickRGB.Devices.Providers;

namespace SickRGB.Views;

public partial class SettingsPage : UserControl, IRefreshablePage
{
    private readonly AppServices _services = AppServices.Current;
    private bool _loading = true;

    private static readonly int[] FpsChoices = { 8, 15, 30, 45, 60, 90, 120 };

    public SettingsPage()
    {
        InitializeComponent();

        var s = _services.Settings;

        TxtHost.Text = s.OpenRgbHost;
        TxtPort.Text = s.OpenRgbPort.ToString();

        ChkLogitech.IsChecked = s.LogitechExperimental;
        ChkAutoStart.IsChecked = AutoStart.IsEnabled();
        ChkTray.IsChecked = s.MinimiseToTray;
        ChkStartMin.IsChecked = s.StartMinimised;
        ChkMica.IsChecked = s.UseMicaBackdrop;

        foreach (int fps in FpsChoices) CmbFps.Items.Add($"{fps} fps");
        int index = Array.IndexOf(FpsChoices, s.TargetFps);
        CmbFps.SelectedIndex = index >= 0 ? index : 2;

        AboutText.Text = $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}\n" +
                         $"Your settings are kept in {AppSettings.ConfigDirectory}";

        _loading = false;
        UpdateOpenRgbStatus();
    }

    public void OnShown() => UpdateOpenRgbStatus();

    private void UpdateOpenRgbStatus()
    {
        var provider = _services.Registry.GetProvider<OpenRgbProvider>();
        if (provider is null) return;

        int count = _services.Registry.Devices.Count(d => d.ProviderId == provider.Id);

        if (provider.IsAvailable)
        {
            OpenRgbStatus.Text = count == 1
                ? "Connected. 1 device added."
                : $"Connected. {count} devices added.";
            OpenRgbStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            OpenRgbStatus.Text = provider.UnavailableReason;
            OpenRgbStatus.Foreground = (Brush)FindResource("TextTertiaryBrush");
        }
    }

    private void Wizard_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenRgbSetupWindow { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        UpdateOpenRgbStatus();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var provider = _services.Registry.GetProvider<OpenRgbProvider>();
        if (provider is null) return;

        string host = string.IsNullOrWhiteSpace(TxtHost.Text) ? "127.0.0.1" : TxtHost.Text.Trim();
        if (!int.TryParse(TxtPort.Text.Trim(), out int port) || port <= 0 || port > 65535) port = 6742;

        provider.Host = host;
        provider.Port = port;
        _services.Settings.OpenRgbHost = host;
        _services.Settings.OpenRgbPort = port;
        _services.Settings.Save();

        BtnConnect.IsEnabled = false;
        OpenRgbStatus.Text = "Connecting...";
        try
        {
            await _services.Engine.RescanAsync();
        }
        finally
        {
            BtnConnect.IsEnabled = true;
            UpdateOpenRgbStatus();
        }
    }

    private async void Logitech_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool on = ChkLogitech.IsChecked == true;
        _services.Settings.LogitechExperimental = on;
        LogitechProvider.Enabled = on;
        _services.Settings.Save();

        await _services.Engine.RescanAsync();
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = ChkAutoStart.IsChecked == true;
        AutoStart.Set(on);
        _services.Settings.StartWithWindows = on;
        _services.Settings.Save();
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.MinimiseToTray = ChkTray.IsChecked == true;
        _services.Settings.Save();
    }

    private void StartMin_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.StartMinimised = ChkStartMin.IsChecked == true;
        _services.Settings.Save();
    }

    private void Mica_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.UseMicaBackdrop = ChkMica.IsChecked == true;
        _services.Settings.Save();
    }

    private void Fps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int index = CmbFps.SelectedIndex;
        if (index < 0 || index >= FpsChoices.Length) return;
        _services.Settings.TargetFps = FpsChoices[index];
        _services.Settings.Save();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticsWindow { Owner = Window.GetWindow(this) };
        window.Show();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.ConfigDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppSettings.ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] could not open config folder: {ex.Message}");
        }
    }
}
