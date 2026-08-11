using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SickRGB.Core;
using SickRGB.Input;
using SickRGB.Views;

namespace SickRGB;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly DispatcherTimer _hookTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private DevicesPage? _devicesPage;
    private LayoutPage? _layoutPage;
    private EffectsPage? _effectsPage;
    private SettingsPage? _settingsPage;

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _reallyExiting;

    public MainWindow()
    {
        InitializeComponent();

        _services = AppServices.Current;

        _keyboardHook.KeyStruck += x => _services.Engine.PushKey(x);
        _mouseHook.Clicked += () => _services.Engine.PushClick();

        _services.Engine.DevicesChanged += OnDevicesChanged;

        SetUpTray();

        NavList.SelectedIndex = 0;

        // Reactive effects need input hooks; non-reactive ones must not hold them.
        _hookTimer.Tick += (_, _) => SyncInputHooks();
        _hookTimer.Start();

        _services.Engine.Start();
        _ = InitialScanAsync();
    }

    private async Task InitialScanAsync()
    {
        await _services.Engine.RescanAsync();
        Dispatcher.Invoke(UpdateStatus);
    }

    // ================================================================== navigation

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;

        PageHost.Content = tag switch
        {
            "devices" => _devicesPage ??= new DevicesPage(),
            "layout" => _layoutPage ??= new LayoutPage(),
            "effects" => _effectsPage ??= new EffectsPage(),
            "settings" => _settingsPage ??= new SettingsPage(),
            _ => PageHost.Content,
        };

        // Pages refresh themselves when shown, so a rescan elsewhere is picked up.
        if (PageHost.Content is IRefreshablePage page) page.OnShown();
    }

    private void OnDevicesChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            if (PageHost.Content is IRefreshablePage page) page.OnShown();
        });
    }

    private void UpdateStatus()
    {
        var devices = _services.Registry.Devices;
        int enabled = devices.Count(d => d.Enabled);
        int lights = devices.Where(d => d.Enabled).Sum(d => d.ZoneCount);

        if (devices.Count == 0)
        {
            StatusText.Text = "No devices found";
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
        }
        else
        {
            StatusText.Text = $"{enabled} device{(enabled == 1 ? "" : "s")}, {lights} light{(lights == 1 ? "" : "s")}";
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
        }

        if (_tray is not null) _tray.Text = $"SickRGB - {StatusText.Text}";
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        BtnRescan.IsEnabled = false;
        StatusText.Text = "Scanning...";
        try { await _services.Engine.RescanAsync(); }
        finally
        {
            BtnRescan.IsEnabled = true;
            UpdateStatus();
        }
    }

    private void AddLights_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenRgbSetupWindow { Owner = this };
        dialog.ShowDialog();
        UpdateStatus();
        if (PageHost.Content is IRefreshablePage page) page.OnShown();
    }

    /// <summary>Installs or removes the global hooks to match what the active effects need.</summary>
    private void SyncInputHooks()
    {
        bool wanted = _services.Engine.NeedsInputHooks;

        if (wanted)
        {
            if (!_keyboardHook.IsInstalled) _keyboardHook.Install();
            if (!_mouseHook.IsInstalled) _mouseHook.Install();
        }
        else
        {
            if (_keyboardHook.IsInstalled) _keyboardHook.Uninstall();
            if (_mouseHook.IsInstalled) _mouseHook.Uninstall();
        }
    }

    // ================================================================== window chrome

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Swap the glyph between "maximise" and "restore".
        BtnMaximise.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        TryEnableDarkTitleBar(hwnd);
        TryEnableMica(hwnd);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;    // Mica

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private static void TryEnableDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));   // pre-release attribute id
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>
    /// Applies the Mica backdrop on Windows 11 22H2 and later.
    ///
    /// All three steps are required and in this order: DWM must be told to draw its
    /// backdrop across the whole client area, the backdrop type must be accepted, and
    /// only then may the WPF surface be made transparent. Skipping the frame extension
    /// leaves a transparent window with nothing painted behind it, which renders black.
    ///
    /// If any step fails - or the user has turned Mica off - the window keeps its solid
    /// surface, which always renders correctly.
    /// </summary>
    private void TryEnableMica(IntPtr hwnd)
    {
        if (!_services.Settings.UseMicaBackdrop) return;

        try
        {
            if (Environment.OSVersion.Version.Build < 22621) return;

            // 1. Extend the DWM frame across the entire client area.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;

            // 2. Ask for the Mica backdrop.
            int backdrop = DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
            {
                // Undo the frame extension so we do not leave a transparent border behind.
                var reset = new MARGINS();
                DwmExtendFrameIntoClientArea(hwnd, ref reset);
                return;
            }

            // 3. Let the backdrop show through WPF's otherwise opaque surface.
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                Background = Brushes.Transparent;
            }
        }
        catch
        {
            // Any failure at all: fall back to the solid surface.
            Background = (Brush)FindResource("AppBackgroundBrush");
        }
    }

    // ================================================================== tray

    private void SetUpTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = BuildTrayIcon(),
                Visible = true,
                Text = "SickRGB",
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open SickRGB", null, (_, _) => RevealWindow());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => RevealWindow();
        }
        catch { /* the app is still fully usable without a tray icon */ }
    }

    /// <summary>
    /// Loads the app icon for the notification area, so the tray, taskbar and window
    /// all show the same thing.
    /// </summary>
    private static System.Drawing.Icon BuildTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var resource = Application.GetResourceStream(uri);
            if (resource?.Stream is { } stream)
            {
                using (stream)
                {
                    // Ask for the small variant so it stays crisp in the tray.
                    return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Tray] could not load the app icon: {ex.Message}");
        }

        // Fallback: draw something rather than showing no tray icon at all.
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 32, 32),
                System.Drawing.Color.FromArgb(255, 176, 32),
                System.Drawing.Color.FromArgb(60, 120, 255),
                System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
            g.FillEllipse(brush, 2, 2, 28, 28);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Brings the window up and to the front. Used by the tray icon and by a second
    /// launch of the app (taskbar, Start menu, shortcut).
    /// </summary>
    public void RevealWindow()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        // Windows blocks a background process from taking focus outright; a brief
        // topmost flip is the reliable way to surface the window.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _reallyExiting = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExiting && _services.Settings.MinimiseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _hookTimer.Stop();
        // Flush synchronously: ordinary saves are debounced and would otherwise be lost.
        _services.Settings.SaveNow();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _services.Engine.Dispose();
        _services.Registry.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}

/// <summary>Implemented by pages that need to refresh when shown or when devices change.</summary>
public interface IRefreshablePage
{
    void OnShown();
}
