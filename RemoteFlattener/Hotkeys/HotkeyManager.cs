using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RemoteFlattener.Interop;
using RemoteFlattener.Logging;
using RemoteFlattener.VirtualDesktop;
using static RemoteFlattener.Interop.Win32Input;

namespace RemoteFlattener.Hotkeys;

/// <summary>The action the keyboard hook callback should take for a given key event.</summary>
internal enum HotkeyAction
{
    /// <summary>Pass this event to the next hook — take no special action.</summary>
    PassThrough,
    /// <summary>Eat Tab keyup while Win is held (prevents ghost Start-menu events).</summary>
    EatTabUp,
    /// <summary>Eat the event and show the virtual desktop overlay.</summary>
    WinTab,
    /// <summary>Pass through and schedule a desktop-edge check for left.</summary>
    CtrlWinLeft,
    /// <summary>Pass through and schedule a desktop-edge check for right.</summary>
    CtrlWinRight,
}

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
            bool isInjected = (ks.flags & LLKHF_INJECTED) != 0;
            int  vk         = (int)ks.vkCode;
            bool isDown     = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp       = wParam == (IntPtr)WM_KEYUP   || wParam == (IntPtr)WM_SYSKEYUP;

            switch (DecideAction(nCode, isInjected, vk, isDown, isUp, IsWinDown(), IsCtrlDown()))
            {
                case HotkeyAction.EatTabUp:
                    return new IntPtr(1);

                case HotkeyAction.WinTab:
                    // Eat Win+Tab and show our overlay.  Inject a decoy key first so Windows
                    // doesn't treat the subsequent Win keyup as a Start menu shortcut.
                    AppLogger.Log("Hotkey: Win+Tab → toggling overlay.");
                    InjectDecoyKey();
                    WinTabPressed?.Invoke();
                    return new IntPtr(1);

                case HotkeyAction.CtrlWinLeft:
                    AppLogger.Log("Hotkey: Ctrl+Win+Left detected — scheduling edge check.");
                    ScheduleEdgeCheck(left: true);
                    break;

                case HotkeyAction.CtrlWinRight:
                    AppLogger.Log("Hotkey: Ctrl+Win+Right detected — scheduling edge check.");
                    ScheduleEdgeCheck(left: false);
                    break;
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ScheduleEdgeCheck(bool left)
    {
        var desktopBefore = VirtualDesktopHelper.GetCurrentDesktopGuid();
        Task.Delay(300).ContinueWith(
            _ => HandleEdgeCheck(left, desktopBefore, VirtualDesktopHelper.GetCurrentDesktopGuid()),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Pure decision function for the keyboard hook callback.  Returns what action to take
    /// given the current key event and modifier state.  Extracted for unit testing.
    /// </summary>
    internal static HotkeyAction DecideAction(
        int nCode, bool isInjected, int vkCode, bool isDown, bool isUp,
        bool isWinDown, bool isCtrlDown)
    {
        if (nCode < 0 || isInjected) return HotkeyAction.PassThrough;

        // Block Tab keyup while Win is held (prevents ghost key events).
        if (isUp && vkCode == VK_TAB && isWinDown) return HotkeyAction.EatTabUp;

        if (isDown && isWinDown)
        {
            if (vkCode == VK_TAB)                           return HotkeyAction.WinTab;
            if (vkCode == VK_LEFT  && isCtrlDown)           return HotkeyAction.CtrlWinLeft;
            if (vkCode == VK_RIGHT && isCtrlDown)           return HotkeyAction.CtrlWinRight;
        }

        return HotkeyAction.PassThrough;
    }

    /// <summary>
    /// Processes the result of the 300ms edge check: fires the appropriate event when
    /// the desktop GUID has not changed (the user was at the leftmost/rightmost edge).
    /// Extracted for unit testing without a real timer or VirtualDesktop API.
    /// </summary>
    internal void HandleEdgeCheck(bool left, Guid before, Guid after)
    {
        if (after == before)
        {
            AppLogger.Log($"Hotkey: Ctrl+Win+{(left ? "Left" : "Right")} at desktop edge \u2014 broadcasting switch to peers.");
            if (left) SwitchDesktopLeft?.Invoke();
            else      SwitchDesktopRight?.Invoke();
        }
        else
        {
            AppLogger.Log($"Hotkey: Ctrl+Win+{(left ? "Left" : "Right")} \u2014 desktop changed locally, no broadcast needed.");
        }
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
