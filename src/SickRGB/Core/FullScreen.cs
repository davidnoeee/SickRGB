using System.Runtime.InteropServices;
using System.Text;

namespace SickRGB.Core;

/// <summary>
/// Tells whether the program with focus is running full screen - in practice, a game.
///
/// Two checks, because neither covers every case on its own. Windows' notification state
/// knows about exclusive full-screen Direct3D and presentation mode, while the window test
/// catches borderless full-screen games, which are ordinary windows that happen to cover
/// their whole monitor.
/// </summary>
public static class FullScreen
{
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    public static bool IsFocused()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0 &&
                state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode)
                return true;

            return CoversItsMonitor(GetForegroundWindow());
        }
        catch
        {
            // Not knowing is treated as no game: the lights simply keep animating.
            return false;
        }
    }

    /// <summary>
    /// Whether a window is borderless full screen: no title bar, covering its whole
    /// monitor, and not the desktop itself.
    /// </summary>
    public static bool CoversItsMonitor(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;

            // The desktop behind the icons is a window covering the whole monitor too.
            var cls = new StringBuilder(64);
            GetClassNameW(hwnd, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW") return false;

            // A maximised window keeps its title bar; with an auto-hiding taskbar it can
            // cover the monitor exactly, and it is not a game.
            if ((GetWindowLongPtrW(hwnd, GwlStyle).ToInt64() & WsCaption) == WsCaption) return false;

            if (!GetWindowRect(hwnd, out var window)) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return false;

            return window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top
                && window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
