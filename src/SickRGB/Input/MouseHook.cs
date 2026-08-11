using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SickRGB.Input;

/// <summary>
/// Low-level mouse hook, used so a click can seed an effect from the mouse's position
/// on the canvas.
///
/// PRIVACY: only button-down events are observed, and nothing about them is kept -
/// no coordinates, no button identity, no timing history. The hook raises a bare
/// notification and nothing else. Movement and scroll events are ignored entirely,
/// and every event is passed straight on to the rest of the system.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Held in a field so the GC cannot collect the delegate Windows holds a pointer to.
    private readonly HookProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>Raised when any mouse button goes down. Carries no data by design.</summary>
    public event Action? Clicked;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public MouseHook() => _proc = HookCallback;

    /// <summary>Installs the hook. Must be called from a thread that pumps messages.</summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Debug.WriteLine($"[MouseHook] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        return _hook != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                try { Clicked?.Invoke(); }
                catch (Exception ex) { Debug.WriteLine($"[MouseHook] listener threw: {ex.Message}"); }
            }
        }

        // Always pass the event on - we observe, never intercept.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
