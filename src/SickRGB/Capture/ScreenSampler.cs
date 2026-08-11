using System.Runtime.InteropServices;
using SickRGB.Core;

namespace SickRGB.Capture;

/// <summary>A monitor (or the whole virtual desktop) that ambient mode can sample.</summary>
public sealed record CaptureTarget(string Name, int X, int Y, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>
/// Captures the screen once per frame into a small colour grid, then answers
/// "what colour is the screen at this point?" for each light.
///
/// Working as a 2D grid rather than a row of columns is what lets a light placed at
/// the bottom-right of the canvas pick up the bottom-right of the screen.
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    // Raw capture resolution, then box-averaged down into the lookup grid.
    private const int SampleWidth = 192;
    private const int SampleHeight = 108;
    private const int GridWidth = 64;
    private const int GridHeight = 36;
    private const int Block = SampleWidth / GridWidth;      // 3x3 pixels per grid cell

    /// <summary>Half-width of the averaging window used by <see cref="ColorAt"/>, in grid cells.</summary>
    private const int LookupRadius = 3;

    private const int SRCCOPY = 0x00CC0020;
    private const int COLORONCOLOR = 3;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)] public byte[] bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd,
                                                                   IntPtr src, int xs, int ys, int ws, int hs, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, int usage,
                                                                           out IntPtr bits, IntPtr section, uint offset);

    private IntPtr _screenDc, _memDc, _dib, _oldObj, _bits;
    private readonly byte[] _raw = new byte[SampleWidth * SampleHeight * 4];
    private readonly RgbF[] _grid = new RgbF[GridWidth * GridHeight];
    private bool _ready;
    private bool _hasPrevious;

    public ScreenSampler() => Initialise();

    private void Initialise()
    {
        Release();

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return;

        _memDc = CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) return;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = SampleWidth,
                biHeight = -SampleHeight,       // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new byte[256 * 4],
        };

        _dib = CreateDIBSection(_memDc, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) return;

        _oldObj = SelectObject(_memDc, _dib);
        SetStretchBltMode(_memDc, COLORONCOLOR);
        _ready = true;
    }

    public static List<CaptureTarget> EnumerateTargets()
    {
        var list = new List<CaptureTarget>();
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                string label = screens[i].Primary ? $"Display {i + 1} (primary)" : $"Display {i + 1}";
                list.Add(new CaptureTarget($"{label}  {b.Width} x {b.Height}", b.X, b.Y, b.Width, b.Height));
            }

            if (screens.Length > 1)
            {
                var v = System.Windows.Forms.SystemInformation.VirtualScreen;
                list.Add(new CaptureTarget($"All displays  {v.Width} x {v.Height}", v.X, v.Y, v.Width, v.Height));
            }
        }
        catch { /* fall through to a safe default */ }

        if (list.Count == 0) list.Add(new CaptureTarget("Primary display", 0, 0, 1920, 1080));
        return list;
    }

    /// <summary>
    /// Grabs one frame. Call once per render frame, before any <see cref="ColorAt"/> lookups.
    /// </summary>
    /// <param name="smoothing">0 = instant, approaching 1 = heavily damped.</param>
    public bool Capture(CaptureTarget target, double smoothing)
    {
        if (!_ready)
        {
            Initialise();
            if (!_ready) return false;
        }

        if (target.Width <= 0 || target.Height <= 0) return false;

        if (!StretchBlt(_memDc, 0, 0, SampleWidth, SampleHeight,
                        _screenDc, target.X, target.Y, target.Width, target.Height, SRCCOPY))
        {
            // The desktop DC goes stale on resolution change or a secure-desktop switch.
            Initialise();
            return false;
        }

        Marshal.Copy(_bits, _raw, 0, _raw.Length);

        double blend = _hasPrevious ? 1.0 - Math.Clamp(smoothing, 0, 0.97) : 1.0;

        for (int gy = 0; gy < GridHeight; gy++)
        {
            for (int gx = 0; gx < GridWidth; gx++)
            {
                double r = 0, g = 0, b = 0;

                for (int sy = 0; sy < Block; sy++)
                {
                    int row = (gy * Block + sy) * SampleWidth * 4;
                    for (int sx = 0; sx < Block; sx++)
                    {
                        int p = row + (gx * Block + sx) * 4;
                        b += _raw[p];               // DIB order is BGRA
                        g += _raw[p + 1];
                        r += _raw[p + 2];
                    }
                }

                const double n = Block * Block * 255.0;
                var fresh = new RgbF(r / n, g / n, b / n);

                int idx = gy * GridWidth + gx;
                _grid[idx] = _hasPrevious ? _grid[idx].Lerp(fresh, blend) : fresh;
            }
        }

        _hasPrevious = true;
        return true;
    }

    /// <summary>
    /// Average screen colour around a normalised point, (0,0) top-left to (1,1) bottom-right.
    /// Averaging a window rather than a single cell keeps neighbouring lights from
    /// flickering independently on fine detail.
    /// </summary>
    public RgbF ColorAt(double u, double v)
    {
        int cx = (int)Math.Round(Math.Clamp(u, 0, 1) * (GridWidth - 1));
        int cy = (int)Math.Round(Math.Clamp(v, 0, 1) * (GridHeight - 1));

        double r = 0, g = 0, b = 0;
        int count = 0;

        for (int y = cy - LookupRadius; y <= cy + LookupRadius; y++)
        {
            if (y < 0 || y >= GridHeight) continue;
            for (int x = cx - LookupRadius; x <= cx + LookupRadius; x++)
            {
                if (x < 0 || x >= GridWidth) continue;
                var c = _grid[y * GridWidth + x];
                r += c.R; g += c.G; b += c.B;
                count++;
            }
        }

        if (count == 0) return RgbF.Black;
        return new RgbF(r / count, g / count, b / count);
    }

    public void ResetSmoothing() => _hasPrevious = false;

    private void Release()
    {
        _ready = false;
        if (_memDc != IntPtr.Zero && _oldObj != IntPtr.Zero) SelectObject(_memDc, _oldObj);
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        _oldObj = IntPtr.Zero;
        _bits = IntPtr.Zero;
    }

    public void Dispose() => Release();
}
