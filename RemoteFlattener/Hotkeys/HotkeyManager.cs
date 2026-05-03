using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener.Hotkeys;

/// <summary>
/// Installs a WH_KEYBOARD_LL hook to monitor Win+Left, Win+Right, and Win+Tab.
/// Must be installed from the WPF main (STA, message-loop) thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_LWIN  = 0x5B;
    private const int VK_RWIN  = 0x5C;
    private const int VK_LEFT  = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_TAB   = 0x09;

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
    /// <summary>Raised when Win+Left was pressed and the desktop did not change (already leftmost).</summary>
    public event Action? SwitchDesktopLeft;
    /// <summary>Raised when Win+Right was pressed and the desktop did not change (already rightmost).</summary>
    public event Action? SwitchDesktopRight;

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private bool IsWinDown() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)ks.vkCode;

            if (IsWinDown())
            {
                switch (vk)
                {
                    case VK_TAB:
                        // Block Win+Tab entirely and show the overlay.
                        WinTabPressed?.Invoke();
                        return new IntPtr(1);

                    case VK_LEFT:
                        // Let the key pass; after 300ms check if desktop changed.
                        ScheduleEdgeCheck(left: true);
                        break;

                    case VK_RIGHT:
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
                if (left)
                    SwitchDesktopLeft?.Invoke();
                else
                    SwitchDesktopRight?.Invoke();
            }
        }, TaskScheduler.Default);
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
