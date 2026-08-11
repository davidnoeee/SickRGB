using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SickRGB.Core;
using SickRGB.Devices.Providers;
using SickRGB.OpenRgb;

namespace SickRGB.Views;

public partial class OpenRgbSetupWindow : Window
{
    private readonly AppServices _services = AppServices.Current;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private OpenRgbState _state = OpenRgbState.NotInstalled;

    /// <summary>
    /// Set when the administrator choice is changed while OpenRGB is already running.
    /// Privileges are fixed at launch, so the only way to apply it is a restart.
    /// </summary>
    private bool _needsRestartForElevation;

    public OpenRgbSetupWindow()
    {
        InitializeComponent();

        SourceNote.Text = $"OpenRGB {OpenRgbSetup.Version}, downloaded from codeberg.org\n" +
                          $"Unpacked to {OpenRgbSetup.InstallDirectory}";

        Loaded += (_, _) => RefreshState();
    }

    // ================================================================== state

    /// <summary>
    /// Probes OpenRGB off the UI thread. Checking it opens a socket and walks the
    /// process list, which would otherwise freeze the window briefly.
    /// </summary>
    private async void RefreshState()
    {
        var s = _services.Settings;
        _state = await Task.Run(() => OpenRgbSetup.GetState(s.OpenRgbHost, s.OpenRgbPort));

        switch (_state)
        {
            case OpenRgbState.Ready:
                SetState("SuccessBrush", "OpenRGB is running and connected",
                         "Its lights are available in SickRGB. Keep the OpenRGB window open.");
                break;

            case OpenRgbState.RunningNoServer:
                SetState("WarningBrush", "OpenRGB is running, but not reachable",
                         "Nothing is answering on the local port. Restarting it usually fixes this.");
                break;

            case OpenRgbState.InstalledNotRunning:
                SetState("TextTertiaryBrush", "OpenRGB is installed but not running",
                         "Start it to add its lights to your layout.");
                break;

            default:
                SetState("TextTertiaryBrush", "OpenRGB is not installed",
                         "It will be downloaded and unpacked into this app's own folder.");
                break;
        }

        BtnReinstall.IsEnabled = _state != OpenRgbState.NotInstalled;
        BtnReinstall.Visibility = _state == OpenRgbState.NotInstalled ? Visibility.Collapsed : Visibility.Visible;

        UpdatePrimaryButton();
        RefreshPawnIoState();
    }

    private void UpdatePrimaryButton()
    {
        bool running = _state is OpenRgbState.Ready or OpenRgbState.RunningNoServer;

        BtnGo.Content = _state switch
        {
            OpenRgbState.NotInstalled => "Download and start",
            OpenRgbState.InstalledNotRunning => "Start OpenRGB",
            OpenRgbState.RunningNoServer => "Restart OpenRGB",
            _ when _needsRestartForElevation => "Restart OpenRGB",
            _ => "Reconnect and scan",
        };

        ElevateNote.Visibility = running && _needsRestartForElevation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetState(string brushKey, string title, string detail)
    {
        StateDot.Fill = (Brush)FindResource(brushKey);
        StateTitle.Text = title;
        StateDetail.Text = detail;
    }

    private void RefreshPawnIoState()
    {
        bool installed = OpenRgbSetup.IsPawnIoInstalled();

        PawnIoDot.Fill = (Brush)FindResource(installed ? "SuccessBrush" : "TextTertiaryBrush");
        PawnIoTitle.Text = installed
            ? "Driver installed - memory and motherboard lighting can be reached"
            : "Driver for memory and motherboard lighting (not installed)";
        BtnPawnIo.Content = installed ? "Reinstall the driver" : "Get the PawnIO installer";
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        // Privileges are decided when the process starts, so a change only takes effect
        // after a restart.
        if (_state is OpenRgbState.Ready or OpenRgbState.RunningNoServer)
            _needsRestartForElevation = true;

        UpdatePrimaryButton();
    }

    // ================================================================== actions

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunSetupAsync(reinstall: false);
    }

    private async void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var confirm = MessageBox.Show(
            "This closes OpenRGB, deletes the copy in this app's folder, and downloads a fresh one.\n\n" +
            "Your OpenRGB settings and device layout are not touched.",
            "Reinstall OpenRGB", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;
        await RunSetupAsync(reinstall: true);
    }

    private async Task RunSetupAsync(bool reinstall)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        BtnCancel.IsEnabled = true;
        SetBusy(true);

        var s = _services.Settings;
        bool elevate = ChkElevate.IsChecked == true;

        try
        {
            var progress = new Progress<(string Status, double? Fraction)>(p =>
            {
                ProgressText.Text = p.Status;
                ProgressFill.Width = p.Fraction is { } f
                    ? Math.Max(0, f) * Math.Max(ProgressArea.ActualWidth, 1)
                    : ProgressFill.Width;
            });

            bool restarting = reinstall || _needsRestartForElevation || _state == OpenRgbState.RunningNoServer;

            // ---- close the running copy when we need to replace or restart it ----
            if (restarting && OpenRgbSetup.IsProcessRunning())
            {
                ProgressText.Text = "Closing OpenRGB...";
                if (!await OpenRgbSetup.StopOpenRgbAsync(_cts.Token))
                {
                    // An elevated OpenRGB cannot be closed by a non-elevated app.
                    ProgressText.Text = "OpenRGB could not be closed automatically, most likely because it is " +
                                        "running as administrator. Close it yourself, then try again.";
                    return;
                }
            }

            // ---- download when missing, or when reinstalling ----
            if (reinstall || _state == OpenRgbState.NotInstalled)
            {
                await OpenRgbSetup.DownloadAndExtractAsync(progress, _cts.Token, clean: reinstall);
            }

            // ---- start it if it is not already answering ----
            bool reachable = await Task.Run(() => OpenRgbSetup.IsServerReachable(s.OpenRgbHost, s.OpenRgbPort));
            if (!reachable)
            {
                ProgressText.Text = elevate
                    ? "Starting OpenRGB - accept the administrator prompt..."
                    : "Starting OpenRGB...";
                ProgressFill.Width = 0;

                if (!OpenRgbSetup.Launch(s.OpenRgbPort, elevate, out string error))
                {
                    ProgressText.Text = error;
                    return;
                }

                ProgressText.Text = "Waiting for OpenRGB to finish starting up...";
                if (!await OpenRgbSetup.WaitForServerAsync(s.OpenRgbHost, s.OpenRgbPort, 40000, _cts.Token))
                {
                    ProgressText.Text = "OpenRGB started but is not answering yet. If it is showing a dialog, " +
                                        "deal with that first, then press Reconnect and scan.";
                    return;
                }
            }

            _needsRestartForElevation = false;
            await ConnectAndRescanAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Setup failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            _busy = false;
            RefreshState();
        }
    }

    private async Task ConnectAndRescanAsync()
    {
        ProgressFill.Width = 0;

        var s = _services.Settings;
        if (_services.Registry.GetProvider<OpenRgbProvider>() is { } provider)
        {
            provider.Host = s.OpenRgbHost;
            provider.Port = s.OpenRgbPort;
        }

        // OpenRGB starts answering its port well before it has finished probing hardware,
        // so the first scan usually comes back empty. Keep trying rather than making the
        // user press Rescan until it happens to work.
        int count = 0;
        var deadline = DateTime.UtcNow.AddSeconds(60);

        for (int attempt = 1; DateTime.UtcNow < deadline; attempt++)
        {
            if (_cts?.IsCancellationRequested == true) break;

            ProgressText.Text = attempt == 1
                ? "Looking for lights..."
                : $"Still looking for lights... (check {attempt})";

            // Creep the bar along so it is clear something is happening.
            double progress = Math.Min(0.9, attempt / 14.0);
            ProgressFill.Width = progress * Math.Max(ProgressArea.ActualWidth, 1);

            await _services.Engine.RescanAsync();
            count = _services.Registry.Devices.Count(d => d.ProviderId == "openrgb");
            if (count > 0) break;

            try { await Task.Delay(2500, _cts?.Token ?? CancellationToken.None); }
            catch (OperationCanceledException) { break; }
        }

        ProgressFill.Width = Math.Max(ProgressArea.ActualWidth, 1);

        if (count > 0)
        {
            int unsized = _services.Registry.Devices
                .Where(d => d.ProviderId == "openrgb")
                .Sum(d => d.ResizableHeaders.Count(h => h.CurrentLeds == 0));

            ProgressText.Text = unsized > 0
                ? $"Added {count} device{(count == 1 ? "" : "s")}. {unsized} addressable header{(unsized == 1 ? " has" : "s have")} " +
                  "no strip length set - open Devices to enter how many LEDs are connected."
                : $"Added {count} device{(count == 1 ? "" : "s")}. Open the Layout page to arrange them.";
        }
        else
        {
            ProgressText.Text = "Connected, but OpenRGB has not reported any devices. Memory and motherboard " +
                                "lighting need the PawnIO driver and administrator rights.";
        }
    }

    // ================================================================== PawnIO

    private async void PawnIo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        BtnCancel.IsEnabled = true;
        SetBusy(true);

        try
        {
            var progress = new Progress<(string Status, double? Fraction)>(p =>
            {
                ProgressText.Text = p.Status;
                ProgressFill.Width = p.Fraction is { } f
                    ? Math.Max(0, f) * Math.Max(ProgressArea.ActualWidth, 1)
                    : ProgressFill.Width;
            });

            string installer = await OpenRgbSetup.DownloadPawnIoAsync(progress, _cts.Token);

            ProgressText.Text = "Starting the installer. Accept the prompt from Windows, " +
                                "then restart OpenRGB so it can pick up the driver.";

            if (!OpenRgbSetup.RunPawnIoInstaller(installer, out string error))
                ProgressText.Text = error;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Could not download the driver: {ex.Message}. " +
                                $"You can get it yourself from {OpenRgbSetup.PawnIoSiteUrl}";
        }
        finally
        {
            SetBusy(false);
            _busy = false;
            RefreshPawnIoState();
        }
    }

    private void PawnIoSite_Click(object sender, RoutedEventArgs e) => OpenUrl(OpenRgbSetup.PawnIoSiteUrl);

    private void OpenSite_Click(object sender, RoutedEventArgs e) => OpenUrl(OpenRgbSetup.ProjectUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[Setup] could not open browser: {ex.Message}"); }
    }

    // ================================================================== plumbing

    private void SetBusy(bool busy)
    {
        // Once anything has been reported, the status area stays put so the final
        // message does not vanish the moment the work finishes.
        ProgressArea.Visibility = Visibility.Visible;
        BtnOpenSite.Visibility = Visibility.Collapsed;

        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnGo.IsEnabled = !busy;
        BtnReinstall.IsEnabled = !busy && _state != OpenRgbState.NotInstalled;
        ChkElevate.IsEnabled = !busy;
        BtnPawnIo.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ProgressText.Text = "Stopping...";
        BtnCancel.IsEnabled = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
