using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener.RDP;

/// <summary>
/// Finds mstsc.exe (Remote Desktop Connection) windows and determines which
/// virtual desktop each one lives on, using <see cref="VirtualDesktopProvider.GetDesktopIndexForHwnd"/>.
///
/// mstsc window titles typically look like "MACHINENAME - Remote Desktop Connection"
/// so we match by checking whether the title contains a known peer machine name.
/// </summary>
public static class RdpWindowLocator
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>
    /// Returns a mapping from machine name → 1-based virtual desktop index for every
    /// visible mstsc.exe window whose title contains a known peer machine name.
    /// Returns an empty dictionary if the VirtualDesktop API is unavailable.
    /// </summary>
    public static Dictionary<string, int> GetRdpDesktopMap(IEnumerable<string> knownMachineNames)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!VirtualDesktopProvider.IsAvailable) return result;

        // Build lowercase lookup list so we can match case-insensitively inside window titles.
        var names = new List<string>(knownMachineNames);
        if (names.Count == 0) return result;

        var mstscPids  = GetMstscProcessIds();
        var titleBuf   = new StringBuilder(512);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!mstscPids.Contains(pid)) return true;

            titleBuf.Clear();
            GetWindowTextW(hwnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();
            if (string.IsNullOrEmpty(title)) return true;

            var matchedName = MatchMachineName(title, names);
            if (matchedName != null)
            {
                var idx = VirtualDesktopProvider.GetDesktopIndexForHwnd(hwnd);
                if (idx > 0)
                    result[matchedName] = idx;
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static HashSet<uint> GetMstscProcessIds()
    {
        var set = new HashSet<uint>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("mstsc"))
        {
            try { set.Add((uint)p.Id); }
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>
    /// Returns the first name from <paramref name="names"/> found (case-insensitive) inside
    /// <paramref name="windowTitle"/>, or <see langword="null"/> if none match.
    /// The name must be followed by " - " (the standard mstsc title separator) to avoid
    /// false-positive substring matches (e.g. "PC" matching a "MY-PC" window).
    /// Extracted for unit testing without a real window enumeration.
    /// </summary>
    internal static string? MatchMachineName(string windowTitle, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            // mstsc titles are "MACHINENAME - Remote Desktop Connection".
            // Use StartsWith (not Contains) so "PC" does not match "MY-PC - ..."
            if (windowTitle.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }
}
