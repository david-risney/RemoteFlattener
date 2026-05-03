using System;
using System.Runtime.InteropServices;
using RemoteFlattener.Logging;

namespace RemoteFlattener.VirtualDesktop;

/// <summary>Simulates Ctrl+Win+Left/Right key chords to switch virtual desktops.</summary>
public static class VirtualDesktopSwitcher
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_LCONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_TAB  = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is included so that INPUTUNION is sized to the larger of the two,
    // matching the real Win32 INPUT union layout on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
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
        public uint type;
        public INPUTUNION data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>Opens the Windows Task View (virtual desktop overview) by injecting Win+Tab.
    /// The WH_KEYBOARD_LL hook skips injected keystrokes so this does not recurse.</summary>
    public static void ShowTaskView()
    {
        var inputs = new INPUT[]
        {
            MakeKey(VK_LWIN, false),
            MakeKey(VK_TAB,  false),
            MakeKey(VK_TAB,  true),
            MakeKey(VK_LWIN, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Switches the virtual desktop left, first forcing <paramref name="hwnd"/> to the foreground.
    /// Tries the VirtualDesktop COM API first; falls back to a Ctrl+Win+Left SendInput if unavailable.</summary>
    public static void SwitchLeft(IntPtr hwnd)
    {
        if (VirtualDesktopProvider.IsAvailable && VirtualDesktopProvider.SwitchLeft())
        {
            AppLogger.Log("SwitchLeft via VirtualDesktop API.");
            return;
        }
        AppLogger.Log("SwitchLeft via SendInput fallback.");
        Send(VK_LEFT, hwnd);
    }

    /// <summary>Switches the virtual desktop right, first forcing <paramref name="hwnd"/> to the foreground.
    /// Tries the VirtualDesktop COM API first; falls back to a Ctrl+Win+Right SendInput if unavailable.</summary>
    public static void SwitchRight(IntPtr hwnd)
    {
        if (VirtualDesktopProvider.IsAvailable && VirtualDesktopProvider.SwitchRight())
        {
            AppLogger.Log("SwitchRight via VirtualDesktop API.");
            return;
        }
        AppLogger.Log("SwitchRight via SendInput fallback.");
        Send(VK_RIGHT, hwnd);
    }

    private static void Send(ushort arrowVk, IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            ForceForeground(hwnd);

        var inputs = new INPUT[]
        {
            MakeKey(VK_LCONTROL, false),
            MakeKey(VK_LWIN,     false),
            MakeKey(arrowVk,     false),
            MakeKey(arrowVk,     true),
            MakeKey(VK_LWIN,     true),
            MakeKey(VK_LCONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Reliably steals the foreground from any window by briefly attaching the calling thread’s
    /// input queue to the current foreground thread.  This bypasses the Windows foreground-lock
    /// restriction that prevents background processes from calling SetForegroundWindow directly.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var current    = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;

        var fgThread = GetWindowThreadProcessId(foreground, out _);
        AttachThreadInput(current, fgThread, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        AttachThreadInput(current, fgThread, false);
    }

    private static INPUT MakeKey(ushort vk, bool keyUp) => new INPUT
    {
        type = INPUT_KEYBOARD,
        data = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
            }
        }
    };
}
