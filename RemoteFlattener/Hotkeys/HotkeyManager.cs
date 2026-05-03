using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RemoteFlattener.Logging;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener.Hotkeys;

/// <summary>
/// Installs a WH_KEYBOARD_LL hook to monitor Ctrl+Win+Left, Ctrl+Win+Right, and Win+Tab.
/// Must be installed from the WPF main (STA, message-loop) thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private const int VK_LWIN  = 0x5B;
    private const int VK_RWIN  = 0x5C;
    private const int VK_LEFT  = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_TAB   = 0x09;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // SendInput structs — used to inject a synthetic Win keyup after we eat Win+Tab,
    // so Windows doesn't see a lone Win press+release (which opens the Start menu).
    private const uint INPUT_KEYBOARD   = 1;
    private const uint KEYEVENTF_KEYUP  = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx, dy;
        public uint   mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint      type;
        public INPUTUNION data;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private IntPtr _hookHandle;
    // Keep a strong reference so the GC doesn't collect the delegate while the hook is active.
    private LowLevelKeyboardProc? _hookProc;
    private bool _disposed;

    /// <summary>Raised on the thread pool when Win+Tab is pressed. UI updates must be dispatched.</summary>
    public event Action? WinTabPressed;
    /// <summary>Raised when Ctrl+Win+Left was pressed and the desktop did not change (already leftmost).</summary>
    public event Action? SwitchDesktopLeft;
    /// <summary>Raised when Ctrl+Win+Right was pressed and the desktop did not change (already rightmost).</summary>
    public event Action? SwitchDesktopRight;

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module.ModuleName), 0);
        if (_hookHandle != IntPtr.Zero)
            AppLogger.Log("Hotkey hook installed (Win+Tab, Ctrl+Win+Left, Ctrl+Win+Right).");
        else
            AppLogger.Log($"Hotkey hook installation failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        AppLogger.Log("Hotkey hook uninstalled.");
    }

    private bool IsWinDown() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    private bool IsCtrlDown() =>
        (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip synthetic keystrokes injected via SendInput (e.g. our own Task View invocation).
            if ((ks.flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int vk = (int)ks.vkCode;
            bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp   = wParam == (IntPtr)WM_KEYUP   || wParam == (IntPtr)WM_SYSKEYUP;

            // Block Tab keyup while Win is held (prevents ghost key events).
            if (isUp && vk == VK_TAB && IsWinDown())
                return new IntPtr(1);

            if (isDown && IsWinDown())
            {
                switch (vk)
                {
                    case VK_TAB:
                        // Eat Win+Tab and show our overlay.
                        // Problem: if we eat Tab keydown+keyup but pass the real Win keyup
                        // through, Windows sees Win-down then Win-up with nothing else in
                        // between and opens the Start menu.
                        // Fix: inject a decoy Shift keydown+keyup (flagged INJECTED so our
                        // own hook skips it).  This "contaminates" the Win key sequence —
                        // Windows sees another key between Win-down and Win-up and suppresses
                        // the Start menu.  The real Win keyup still flows through normally so
                        // Win is never stuck down.
                        AppLogger.Log("Hotkey: Win+Tab → toggling overlay.");
                        InjectDecoyKey();
                        WinTabPressed?.Invoke();
                        return new IntPtr(1);

                    case VK_LEFT:
                        // Let the key pass; after 300ms check if desktop changed.
                        if (!IsCtrlDown()) break;
                        AppLogger.Log("Hotkey: Ctrl+Win+Left detected — scheduling edge check.");
                        ScheduleEdgeCheck(left: true);
                        break;

                    case VK_RIGHT:
                        if (!IsCtrlDown()) break;
                        AppLogger.Log("Hotkey: Ctrl+Win+Right detected — scheduling edge check.");
                        ScheduleEdgeCheck(left: false);
                        break;
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ScheduleEdgeCheck(bool left)
    {
        var desktopBefore = VirtualDesktopHelper.GetCurrentDesktopGuid();
        Task.Delay(300).ContinueWith(_ =>
        {
            var desktopAfter = VirtualDesktopHelper.GetCurrentDesktopGuid();
            if (desktopAfter == desktopBefore)
            {
                AppLogger.Log($"Hotkey: Ctrl+Win+{(left ? "Left" : "Right")} at desktop edge — broadcasting switch to peers.");
                if (left)
                    SwitchDesktopLeft?.Invoke();
                else
                    SwitchDesktopRight?.Invoke();
            }
            else
            {
                AppLogger.Log($"Hotkey: Ctrl+Win+{(left ? "Left" : "Right")} — desktop changed locally, no broadcast needed.");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Injects a synthetic unassigned-VK keydown + keyup via SendInput (flagged INJECTED so
    /// our own hook skips both events).  This "contaminates" the current Win key sequence:
    /// Windows sees a key pressed between Win-down and Win-up and therefore does not treat
    /// the Win release as a standalone Start-menu shortcut.
    /// VK 0x88 is in the officially-unassigned range 0x88-0x8F: no keyboard generates it,
    /// no system hotkey or accessibility feature is bound to it.
    /// </summary>
    private void InjectDecoyKey()
    {
        const ushort VK_UNASSIGNED = 0x88;  // officially unassigned, never on any keyboard
        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_UNASSIGNED } } },
            new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_UNASSIGNED, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Uninstall();
            _disposed = true;
        }
    }
}
