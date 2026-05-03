using System;
using System.Runtime.InteropServices;

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

    public static void SwitchLeft() => Send(VK_LEFT);
    public static void SwitchRight() => Send(VK_RIGHT);

    private static void Send(ushort arrowVk)
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
