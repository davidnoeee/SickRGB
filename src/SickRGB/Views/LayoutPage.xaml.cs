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
        settings.Rotation = device.Rotation;
        settings.Scale = device.Scale;
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

        // The transform controls only make sense with something selected.
        TransformBar.Visibility = d is null ? Visibility.Collapsed : Visibility.Visible;

        SelectionText.Text = d is null
            ? ""
            : $"{d.Name}   x {d.X:0}  y {d.Y:0}   {d.ScaledWidth:0} x {d.ScaledHeight:0} mm"
              + (Math.Abs(d.Rotation) > 0.01 ? $"   {d.Rotation:0}°" : "")
              + (Math.Abs(d.Scale - 1.0) > 0.01 ? $"   {d.Scale * 100:0}%" : "");
    }

    // 45 degrees: fine enough to sit a device on a diagonal, coarse enough that a couple
    // of clicks gets you to a right angle.
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Canvas.RotateSelected(-45);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Canvas.RotateSelected(45);
    private void ScaleUp_Click(object sender, RoutedEventArgs e) => Canvas.ScaleSelected(1.1);
    private void ScaleDown_Click(object sender, RoutedEventArgs e) => Canvas.ScaleSelected(1 / 1.1);
    private void ResetTransform_Click(object sender, RoutedEventArgs e) => Canvas.ResetSelectedTransform();

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
