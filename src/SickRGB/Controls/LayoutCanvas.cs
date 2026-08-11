using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SickRGB.Devices;

namespace SickRGB.Controls;

/// <summary>
/// The spatial arrangement surface.
///
/// Every device is drawn as a card at its real position in layout units
/// (1 unit = 1 mm), with its individual lights rendered live in their current
/// colours. Dragging a device changes where its lights sit in the world, which is
/// exactly what effects use for distance - so arranging your desk here is what makes
/// a wave travel from the mouse, across the keyboard, and on to the case.
///
/// Drawn with a direct OnRender pass rather than a visual tree, so hundreds of
/// individual LEDs stay smooth at 30 fps.
/// </summary>
public sealed class LayoutCanvas : FrameworkElement
{
    private const double GridStep = 50;          // layout units between grid lines
    private const double SnapStep = 10;

    private DeviceRegistry? _registry;
    private LightDevice? _dragging;
    private LightDevice? _selected;
    private Point _dragStartScreen;
    private double _dragStartX, _dragStartY;
    private bool _panning;
    private Point _panStart;

    private double _scale = 0.55;
    private double _offsetX, _offsetY;
    private bool _fitPending = true;

    // Brushes are frozen once; they are used on every frame.
    private static readonly Brush GridBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GridStrongBrush = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardBrush = Frozen(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
    private static readonly Brush CardDisabledBrush = Frozen(Color.FromArgb(0x66, 0x2A, 0x2A, 0x2A));
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SubLabelBrush = Frozen(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
    private static readonly Pen CardPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 1.5);

    private Pen _selectedPen = FrozenPen(Color.FromRgb(0xFF, 0x6A, 0x2B), 2);

    /// <summary>Corner grip size, in screen pixels.</summary>
    private const double HandleRadius = 5;

    private static readonly Brush HandleFill = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Pen HandlePen = FrozenPen(Color.FromArgb(0x90, 0x00, 0x00, 0x00), 1);

    // Resize state
    private LightDevice? _resizing;
    private double _resizeStartScale;
    private double _resizeStartDistance;
    private Point _resizeCentre;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }

    public LayoutCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>Raised after a device has been dragged to a new position.</summary>
    public event Action<LightDevice>? DeviceMoved;

    /// <summary>Raised when the selected device changes.</summary>
    public event Action<LightDevice?>? SelectionChanged;

    public bool SnapToGrid { get; set; } = true;

    public LightDevice? SelectedDevice
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            SelectionChanged?.Invoke(value);
            InvalidateVisual();
        }
    }

    public void SetRegistry(DeviceRegistry registry)
    {
        _registry = registry;
        _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>Sets the accent colour used for the selection outline.</summary>
    public void SetAccent(Color color) => _selectedPen = FrozenPen(color, 2);

    /// <summary>Frames every device with a comfortable margin.</summary>
    public void FitToContent()
    {
        var devices = _registry?.Devices;
        if (devices is null || devices.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        double minX = devices.Min(d => d.X);
        double minY = devices.Min(d => d.Y);
        double maxX = devices.Max(d => d.X + d.ScaledWidth);
        double maxY = devices.Max(d => d.Y + d.ScaledHeight);

        // Leave room for the name drawn above each card.
        minY -= 26;

        double spanX = Math.Max(maxX - minX, 1);
        double spanY = Math.Max(maxY - minY, 1);
        const double pad = 60;

        _scale = Math.Min((ActualWidth - pad * 2) / spanX, (ActualHeight - pad * 2) / spanY);
        _scale = Math.Clamp(_scale, 0.05, 4.0);

        _offsetX = (ActualWidth - spanX * _scale) / 2 - minX * _scale;
        _offsetY = (ActualHeight - spanY * _scale) / 2 - minY * _scale;

        _fitPending = false;
        InvalidateVisual();
    }

    private Point WorldToScreen(double x, double y) => new(x * _scale + _offsetX, y * _scale + _offsetY);
    private Point ScreenToWorld(Point p) => new((p.X - _offsetX) / _scale, (p.Y - _offsetY) / _scale);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fitPending) FitToContent();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // A hit-testable background so drags anywhere on the surface register.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_fitPending) FitToContent();

        DrawGrid(dc);

        var devices = _registry?.Devices;
        if (devices is null) return;

        foreach (var device in devices)
            DrawDevice(dc, device);
    }

    private void DrawGrid(DrawingContext dc)
    {
        if (_scale <= 0) return;

        // Coarser grid as you zoom out, so lines never turn into a solid wash.
        double step = GridStep;
        while (step * _scale < 18) step *= 2;

        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(ActualWidth, ActualHeight));

        double startX = Math.Floor(topLeft.X / step) * step;
        double startY = Math.Floor(topLeft.Y / step) * step;

        for (double x = startX; x <= bottomRight.X; x += step)
        {
            double sx = Math.Round(x * _scale + _offsetX) + 0.5;
            bool major = Math.Abs(x % (step * 5)) < 0.001;
            dc.DrawLine(new Pen(major ? GridStrongBrush : GridBrush, 1),
                        new Point(sx, 0), new Point(sx, ActualHeight));
        }

        for (double y = startY; y <= bottomRight.Y; y += step)
        {
            double sy = Math.Round(y * _scale + _offsetY) + 0.5;
            bool major = Math.Abs(y % (step * 5)) < 0.001;
            dc.DrawLine(new Pen(major ? GridStrongBrush : GridBrush, 1),
                        new Point(0, sy), new Point(ActualWidth, sy));
        }
    }

    private void DrawDevice(DrawingContext dc, LightDevice device)
    {
        var topLeft = WorldToScreen(device.X, device.Y);
        double w = device.ScaledWidth * _scale;
        double h = device.ScaledHeight * _scale;
        var rect = new Rect(topLeft.X, topLeft.Y, w, h);

        double radius = Math.Min(8, Math.Min(w, h) / 4);

        // Everything below is drawn in the device's own unrotated frame; the transform
        // turns the finished card, so lights and handles rotate with it for free.
        bool rotated = Math.Abs(device.Rotation) > 0.01;
        if (rotated)
        {
            dc.PushTransform(new RotateTransform(device.Rotation,
                                                 rect.X + rect.Width / 2,
                                                 rect.Y + rect.Height / 2));
        }

        // Card body
        dc.DrawRoundedRectangle(device.Enabled ? CardBrush : CardDisabledBrush, null, rect, radius, radius);

        // Individual lights
        if (device.Enabled)
        {
            foreach (var zone in device.Zones)
            {
                double zx = device.Reversed ? device.Width - zone.LocalX - zone.Width : zone.LocalX;
                var zTopLeft = WorldToScreen(device.X + zx * device.Scale,
                                             device.Y + zone.LocalY * device.Scale);
                double zw = zone.Width * device.Scale * _scale;
                double zh = zone.Height * device.Scale * _scale;

                // Inset slightly so neighbouring lights stay visually distinct.
                double inset = Math.Min(1.5, Math.Min(zw, zh) * 0.12);
                var zr = new Rect(zTopLeft.X + inset, zTopLeft.Y + inset,
                                  Math.Max(zw - inset * 2, 1), Math.Max(zh - inset * 2, 1));

                var c = zone.Current;
                double zradius = Math.Min(4, Math.Min(zr.Width, zr.Height) / 3);

                // A soft halo around lit zones. Flat rectangles make it hard to judge
                // where a wavefront actually is; the bloom makes motion legible so the
                // canvas works as a real preview of timing.
                //
                // A radial gradient fading to fully transparent gives an actual blur.
                // A flat translucent rectangle just looks like a bigger rectangle.
                // Skipped for tiny zones (zoomed far out, or dense LED strips) where the
                // glow would be invisible anyway and the brushes would only cost time.
                double level = (c.R + c.G + c.B) / 765.0;
                if (level > 0.05 && zw >= 6 && zh >= 6)
                {
                    double spread = Math.Min(zw, zh) * 0.6;
                    var glowRect = Rect.Inflate(zr, spread, spread);

                    byte peak = (byte)Math.Clamp(level * 105, 0, 105);
                    var glow = new RadialGradientBrush
                    {
                        GradientOrigin = new Point(0.5, 0.5),
                        Center = new Point(0.5, 0.5),
                        RadiusX = 0.5,
                        RadiusY = 0.5,
                    };
                    glow.GradientStops.Add(new GradientStop(Color.FromArgb(peak, c.R, c.G, c.B), 0.0));
                    glow.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.55), c.R, c.G, c.B), 0.4));
                    glow.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.18), c.R, c.G, c.B), 0.7));
                    glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
                    glow.Freeze();

                    dc.DrawRectangle(glow, null, glowRect);
                }

                var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                brush.Freeze();
                dc.DrawRoundedRectangle(brush, null, zr, zradius, zradius);
            }
        }

        // Outline
        var pen = ReferenceEquals(device, _selected) ? _selectedPen
                : ReferenceEquals(device, _dragging) ? HoverPen
                : CardPen;
        dc.DrawRoundedRectangle(null, pen, rect, radius, radius);

        // Corner grips, only on the selected device.
        if (ReferenceEquals(device, _selected))
        {
            foreach (var corner in CornerPoints(rect))
            {
                dc.DrawEllipse(HandleFill, HandlePen, corner, HandleRadius, HandleRadius);
            }
        }

        if (rotated) dc.Pop();

        // Labels are drawn upright, outside the rotation, so they stay readable however
        // the device is turned.
        var label = MakeText(device.Name, 12.5, device.Enabled ? LabelBrush : SubLabelBrush);
        double labelY = rect.Y - label.Height - 5;
        if (rotated) labelY = Math.Min(labelY, rect.Y + rect.Height / 2 - Math.Max(w, h) / 2 - label.Height - 5);
        dc.DrawText(label, new Point(rect.X, labelY));

        // Role and light count, only when there is room
        if (w > 90)
        {
            string sub = $"{device.Role}  ·  {device.ZoneCount} light{(device.ZoneCount == 1 ? "" : "s")}";
            if (Math.Abs(device.Rotation) > 0.01) sub += $"  ·  {device.Rotation:0}°";
            if (Math.Abs(device.Scale - 1.0) > 0.01) sub += $"  ·  {device.Scale * 100:0}%";

            var subText = MakeText(sub, 10.5, SubLabelBrush);
            dc.DrawText(subText, new Point(rect.X + label.Width + 8, labelY + 1));
        }
    }

    /// <summary>The four corners of a rectangle, clockwise from the top left.</summary>
    private static Point[] CornerPoints(Rect r) => new[]
    {
        new Point(r.Left,  r.Top),
        new Point(r.Right, r.Top),
        new Point(r.Right, r.Bottom),
        new Point(r.Left,  r.Bottom),
    };

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                         FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ------------------------------------------------------------------ interaction

    /// <summary>
    /// Converts a screen point into a device's own unrotated frame, so hit testing can
    /// stay a simple rectangle check no matter how the device is turned.
    /// </summary>
    private Point ToDeviceSpace(LightDevice d, Point screen)
    {
        var world = ScreenToWorld(screen);

        double centreX = d.X + d.ScaledWidth / 2.0;
        double centreY = d.Y + d.ScaledHeight / 2.0;

        double radians = -d.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double dx = world.X - centreX;
        double dy = world.Y - centreY;

        return new Point(centreX + dx * cos - dy * sin,
                         centreY + dx * sin + dy * cos);
    }

    private LightDevice? HitTest(Point screen)
    {
        var devices = _registry?.Devices;
        if (devices is null) return null;

        // Reverse order so the topmost drawn device wins.
        for (int i = devices.Count - 1; i >= 0; i--)
        {
            var d = devices[i];
            var local = ToDeviceSpace(d, screen);

            if (local.X >= d.X && local.X <= d.X + d.ScaledWidth &&
                local.Y >= d.Y && local.Y <= d.Y + d.ScaledHeight)
                return d;
        }
        return null;
    }

    /// <summary>Returns true if the point is over one of the selected device's corner grips.</summary>
    private bool IsOverHandle(LightDevice device, Point screen)
    {
        var local = ToDeviceSpace(device, screen);

        // Grip radius is in screen pixels, so convert it to world units.
        double reach = (HandleRadius + 4) / Math.Max(_scale, 1e-6);

        foreach (var corner in new[]
                 {
                     new Point(device.X, device.Y),
                     new Point(device.X + device.ScaledWidth, device.Y),
                     new Point(device.X + device.ScaledWidth, device.Y + device.ScaledHeight),
                     new Point(device.X, device.Y + device.ScaledHeight),
                 })
        {
            if (Math.Abs(local.X - corner.X) <= reach && Math.Abs(local.Y - corner.Y) <= reach)
                return true;
        }
        return false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var position = e.GetPosition(this);

        // A grip on the already-selected device takes priority: its corners can sit over
        // a neighbouring device, and grabbing that instead would be maddening.
        if (_selected is not null && IsOverHandle(_selected, position))
        {
            StartResize(_selected, position);
            InvalidateVisual();
            return;
        }

        var hit = HitTest(position);
        SelectedDevice = hit;

        if (hit is not null)
        {
            _dragging = hit;
            _dragStartScreen = position;
            _dragStartX = hit.X;
            _dragStartY = hit.Y;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);

        if (_panning)
        {
            _offsetX += pos.X - _panStart.X;
            _offsetY += pos.Y - _panStart.Y;
            _panStart = pos;
            InvalidateVisual();
            return;
        }

        if (_resizing is not null)
        {
            ApplyResize(pos);
            return;
        }

        // Show a grip cursor when hovering a corner of the selected device.
        if (_dragging is null && _selected is not null)
        {
            Cursor = IsOverHandle(_selected, pos) ? Cursors.SizeNWSE : null;
        }

        if (_dragging is null) return;

        double dx = (pos.X - _dragStartScreen.X) / _scale;
        double dy = (pos.Y - _dragStartScreen.Y) / _scale;

        double nx = _dragStartX + dx;
        double ny = _dragStartY + dy;

        if (SnapToGrid && !Keyboard.IsKeyDown(Key.LeftAlt) && !Keyboard.IsKeyDown(Key.RightAlt))
        {
            nx = Math.Round(nx / SnapStep) * SnapStep;
            ny = Math.Round(ny / SnapStep) * SnapStep;
        }

        _dragging.X = nx;
        _dragging.Y = ny;

        // Update live so effects follow the device while it is still being dragged.
        DeviceMoved?.Invoke(_dragging);
        InvalidateVisual();
    }

    /// <summary>
    /// Begins a corner resize. The centre is pinned, so the device grows and shrinks in
    /// place rather than crawling away from the cursor.
    /// </summary>
    private void StartResize(LightDevice device, Point screen)
    {
        _resizing = device;
        _resizeStartScale = device.Scale;
        _resizeCentre = new Point(device.X + device.ScaledWidth / 2.0,
                                  device.Y + device.ScaledHeight / 2.0);

        var world = ScreenToWorld(screen);
        _resizeStartDistance = Math.Max(1e-3, Distance(world, _resizeCentre));

        Cursor = Cursors.SizeNWSE;
        CaptureMouse();
    }

    private void ApplyResize(Point screen)
    {
        if (_resizing is null) return;

        var world = ScreenToWorld(screen);
        double distance = Distance(world, _resizeCentre);

        // Scale is always uniform. Dragging a corner is a request to make the device
        // bigger or smaller, not to distort it - a keyboard stretched to twice its width
        // would put its lights where no lights are.
        double scale = _resizeStartScale * (distance / _resizeStartDistance);
        _resizing.Scale = Math.Clamp(scale, 0.15, 6.0);

        // Keep the centre where it was.
        _resizing.X = _resizeCentre.X - _resizing.ScaledWidth / 2.0;
        _resizing.Y = _resizeCentre.Y - _resizing.ScaledHeight / 2.0;

        DeviceMoved?.Invoke(_resizing);
        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_resizing is not null)
        {
            var resized = _resizing;
            _resizing = null;
            Cursor = null;
            ReleaseMouseCapture();
            DeviceMoved?.Invoke(resized);
            InvalidateVisual();
            return;
        }

        if (_dragging is not null)
        {
            var moved = _dragging;
            _dragging = null;
            ReleaseMouseCapture();
            DeviceMoved?.Invoke(moved);
            InvalidateVisual();
        }
    }

    private void StartPan(Point at)
    {
        _panning = true;
        _panStart = at;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Cursor = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        StartPan(e.GetPosition(this));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        EndPan();
    }

    // Middle-drag pans as well - the usual gesture on a canvas. WPF has no
    // dedicated middle-button override, so it is handled here.
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            StartPan(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            EndPan();
            e.Handled = true;
        }
    }

    /// <summary>Panning must not be left stuck on if capture is taken away.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_panning)
        {
            _panning = false;
            Cursor = null;
        }
        _dragging = null;
        _resizing = null;
    }

    /// <summary>Turns the selected device by <paramref name="degrees"/>.</summary>
    public void RotateSelected(double degrees)
    {
        if (_selected is null) return;

        _selected.Rotation = ((_selected.Rotation + degrees) % 360 + 360) % 360;
        DeviceMoved?.Invoke(_selected);
        InvalidateVisual();
    }

    /// <summary>Scales the selected device by a factor, keeping its centre in place.</summary>
    public void ScaleSelected(double factor)
    {
        if (_selected is null) return;

        double centreX = _selected.X + _selected.ScaledWidth / 2.0;
        double centreY = _selected.Y + _selected.ScaledHeight / 2.0;

        _selected.Scale = Math.Clamp(_selected.Scale * factor, 0.15, 6.0);
        _selected.X = centreX - _selected.ScaledWidth / 2.0;
        _selected.Y = centreY - _selected.ScaledHeight / 2.0;

        DeviceMoved?.Invoke(_selected);
        InvalidateVisual();
    }

    /// <summary>Puts the selected device back to unrotated, original size.</summary>
    public void ResetSelectedTransform()
    {
        if (_selected is null) return;

        double centreX = _selected.X + _selected.ScaledWidth / 2.0;
        double centreY = _selected.Y + _selected.ScaledHeight / 2.0;

        _selected.Rotation = 0;
        _selected.Scale = 1.0;
        _selected.X = centreX - _selected.ScaledWidth / 2.0;
        _selected.Y = centreY - _selected.ScaledHeight / 2.0;

        DeviceMoved?.Invoke(_selected);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var before = ScreenToWorld(e.GetPosition(this));
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _scale = Math.Clamp(_scale * factor, 0.05, 4.0);

        // Keep the point under the cursor stationary while zooming.
        var pos = e.GetPosition(this);
        _offsetX = pos.X - before.X * _scale;
        _offsetY = pos.Y - before.Y * _scale;

        InvalidateVisual();
    }
}
