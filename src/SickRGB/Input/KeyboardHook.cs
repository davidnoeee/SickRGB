using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SickRGB.Input;

/// <summary>
/// Low-level keyboard hook used to drive keypress-reactive effects.
///
/// PRIVACY: this class deliberately never exposes which key was pressed. The
/// virtual-key code is converted to a horizontal board position inside the hook
/// callback and then dropped; only that position is raised to listeners. No key
/// data is stored, buffered, written to disk or sent anywhere, and the hook never
/// swallows input - every event is passed straight on to the rest of the system.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Held in a field so the GC cannot collect the delegate while Windows holds a pointer to it.
    private readonly HookProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>Set of keys currently held, so auto-repeat doesn't spam the effects.</summary>
    private readonly HashSet<uint> _down = new();

    /// <summary>
    /// Raised on a fresh key press. The argument is the horizontal position of the
    /// key across the board, 0.0 (far left) to 1.0 (far right).
    /// </summary>
    public event Action<double>? KeyStruck;

    /// <summary>True while the hook is installed.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Installs the hook. Must be called from a thread that pumps messages
    /// (the WPF UI thread does).
    /// </summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Debug.WriteLine($"[KeyboardHook] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        return _hook != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _down.Clear();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;

            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Only react to the initial press, not to auto-repeat.
                if (_down.Add(info.vkCode))
                {
                    // Key identity is converted to a position here and immediately forgotten.
                    double x = KeyZoneMap.NormalizedX((int)info.vkCode);
                    try { KeyStruck?.Invoke(x); }
                    catch (Exception ex) { Debug.WriteLine($"[KeyboardHook] listener threw: {ex.Message}"); }
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                _down.Remove(info.vkCode);
            }
        }

        // Always pass the event on - we observe, never intercept.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
