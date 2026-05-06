using System;
using System.Runtime.InteropServices;

namespace RemoteFlattener.Interop;

/// <summary>
/// Shared Win32 input structures and the SendInput P/Invoke declaration.
/// Centralizes definitions that were previously duplicated in HotkeyManager and VirtualDesktopSwitcher.
/// </summary>
internal static class Win32Input
{
    public const uint INPUT_KEYBOARD  = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is included so that INPUTUNION is sized to the larger of the two,
    // matching the real Win32 INPUT union layout on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int    dx, dy;
        public uint   mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint       type;
        public INPUTUNION data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
