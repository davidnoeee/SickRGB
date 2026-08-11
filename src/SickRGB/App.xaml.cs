using System.Windows;
using SickRGB.Core;
using SickRGB.Devices;
using SickRGB.Devices.Providers;
using SickRGB.Effects;

namespace SickRGB;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\SickRGB.SingleInstance";
    private const string ActivateEventName = @"Local\SickRGB.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy may run: two would fight over every device.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Launching again (taskbar, Start menu, shortcut) should bring the running
            // copy to the front rather than nag about it. Signal it and exit quietly.
            TrySignalRunningInstance();
            Shutdown();
            return;
        }

        // The window can be hidden to the tray, so closing it must not end the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        StartActivationListener();
        ApplySystemAccent();

        var settings = AppSettings.Load();
        LogitechProvider.Enabled = settings.LogitechExperimental;

        var registry = new DeviceRegistry();
        if (registry.GetProvider<OpenRgbProvider>() is { } openRgb)
        {
            openRgb.Host = settings.OpenRgbHost;
            openRgb.Port = settings.OpenRgbPort;
            openRgb.Settings = settings;
        }

        AppServices.Initialise(new AppServices
        {
            Settings = settings,
            Registry = registry,
            Engine = new EffectEngine(settings, registry),
        });

        bool startHiddenRequested = e.Args.Any(a =>
            a.Equals("--minimised", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow();
        MainWindow = window;

        // Show or stay hidden, decided once. Showing and then hiding produced a brief
        // empty window flash.
        if (!startHiddenRequested && !settings.StartMinimised) window.Show();
    }

    private static void TrySignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
            signal.Set();
        }
        catch
        {
            // The other instance may still be starting up, or may be running as a
            // different user. Nothing useful to do; exiting quietly is better than an
            // error box.
        }
    }

    /// <summary>Waits for another launch to ask us to come to the front.</summary>
    private void StartActivationListener()
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activateSignal.WaitOne();
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RevealWindow());
                }
                catch
                {
                    return;   // handle disposed during shutdown
                }
            }
        })
        {
            IsBackground = true,
            Name = "SickRGB Activation",
        };

        listener.Start();
    }

    /// <summary>Matches the app's accent colour to the one chosen in Windows.</summary>
    private void ApplySystemAccent()
    {
        var accent = AccentColors.GetAccent();
        Resources["AccentColor"] = accent;
        Resources["AccentLightColor"] = AccentColors.Lighten(accent, 0.18);
        Resources["AccentDarkColor"] = AccentColors.Darken(accent, 0.18);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* already released */ }
        _instanceMutex?.Dispose();
        _activateSignal?.Dispose();
        base.OnExit(e);
    }
}
