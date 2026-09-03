using System;
using System.Runtime.InteropServices;
using RemoteFlattener.Interop;
using RemoteFlattener.Logging;
using static RemoteFlattener.Interop.Win32Input;

namespace RemoteFlattener.VirtualDesktop;

/// <summary>Simulates Ctrl+Win+Left/Right key chords to switch virtual desktops.</summary>
public static class VirtualDesktopSwitcher
{
    private const ushort VK_LCONTROL = 0x11;
    private const ushort VK_LWIN  = 0x5B;
    private const ushort VK_LEFT  = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_TAB   = 0x09;

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

    /// <summary>Switches to the desktop at the given 1-based <paramref name="targetIndex"/>.
    /// Tries the VirtualDesktop COM API first; falls back to SendInput if unavailable.</summary>
    public static void SwitchToIndex(int targetIndex, IntPtr hwnd)
    {
        if (VirtualDesktopProvider.IsAvailable && VirtualDesktopProvider.SwitchToIndex(targetIndex))
        {
            AppLogger.Log($"SwitchToIndex({targetIndex}) via VirtualDesktop API.");
            return;
        }

        // Fallback: read the current index from the registry and navigate with
        // Ctrl+Win+Left/Right keypresses.
        var currentIndex = VirtualDesktopHelper.GetCurrentDesktopIndex();
        var delta = targetIndex - currentIndex;
        if (delta == 0)
        {
            AppLogger.Log($"SwitchToIndex({targetIndex}) — already on target desktop.");
            return;
        }

        AppLogger.Log($"SwitchToIndex({targetIndex}) via SendInput fallback (current={currentIndex}, delta={delta}).");
        var arrowVk = delta > 0 ? VK_RIGHT : VK_LEFT;
        var steps = Math.Abs(delta);

        if (hwnd != IntPtr.Zero)
            ForceForeground(hwnd);

        for (int i = 0; i < steps; i++)
        {
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

            // Small delay between keystrokes so Windows registers each desktop switch.
            if (i < steps - 1)
                System.Threading.Thread.Sleep(100);
        }
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
