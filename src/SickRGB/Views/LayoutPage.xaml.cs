using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SickRGB.Core;
using SickRGB.Devices;

namespace SickRGB.Views;

public partial class LayoutPage : UserControl, IRefreshablePage
{
    private readonly AppServices _services = AppServices.Current;
    private DateTime _lastRepaint = DateTime.MinValue;

    public LayoutPage()
    {
        InitializeComponent();

        Canvas.SetRegistry(_services.Registry);
        if (TryFindResource("AccentColor") is Color accent) Canvas.SetAccent(accent);

        Canvas.DeviceMoved += OnDeviceMoved;
        Canvas.SelectionChanged += OnSelectionChanged;

        // Subscribe on every load, not once in the constructor: page instances are
        // cached and re-used by the shell, so a constructor-only subscription would be
        // dropped by the first Unloaded and never restored - leaving the canvas frozen
        // as soon as you navigate away and come back.
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
        Canvas.SetRegistry(_services.Registry);
        Canvas.FitToContent();
        UpdateSelectionText();
    }

    private void OnDeviceMoved(LightDevice device)
    {
        var settings = _services.Settings.DeviceFor(device.Key);
        settings.X = device.X;
        settings.Y = device.Y;
        settings.HasPlacement = true;

        // Recompute immediately so effects follow the device as it is dragged.
        _services.Engine.LayoutChanged();
        UpdateSelectionText();

        // The settings write is cheap, but not worth doing on every mouse-move frame.
        ScheduleSave();
    }

    private DateTime _nextSave = DateTime.MinValue;

    private void ScheduleSave()
    {
        if (DateTime.UtcNow < _nextSave) return;
        _nextSave = DateTime.UtcNow.AddMilliseconds(400);
        _services.Settings.Save();
    }

    private void OnSelectionChanged(LightDevice? device) => UpdateSelectionText();

    private void UpdateSelectionText()
    {
        var d = Canvas.SelectedDevice;
        SelectionText.Text = d is null
            ? ""
            : $"{d.Name}   x {d.X:0}  y {d.Y:0}   ({d.Width:0} x {d.Height:0} mm)";
    }

    private void OnFrameRendered()
    {
        if (!IsVisible) return;

        // The engine already fires this at ~30 fps. Use a slightly looser gate here so
        // frame jitter does not alias against that cadence and drop repaints; this only
        // exists to stop a fast engine from flooding the UI thread.
        if ((DateTime.UtcNow - _lastRepaint).TotalMilliseconds < 22) return;
        _lastRepaint = DateTime.UtcNow;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                               () => Canvas.InvalidateVisual());
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitToContent();

    private void Arrange_Click(object sender, RoutedEventArgs e)
    {
        _services.Registry.AutoArrange(_services.Settings);
        _services.Engine.LayoutChanged();
        Canvas.FitToContent();
        UpdateSelectionText();
    }

    private void Snap_Click(object sender, RoutedEventArgs e) =>
        Canvas.SnapToGrid = ChkSnap.IsChecked == true;
}
