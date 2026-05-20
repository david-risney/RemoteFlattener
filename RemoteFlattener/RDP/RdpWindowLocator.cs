using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RemoteFlattener.Logging;
using RemoteFlattener.Models;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener.RDP;

/// <summary>
/// Finds mstsc.exe (Remote Desktop Connection) and msrdc.exe (Windows App / Cloud DevBox)
/// windows and determines which virtual desktop each one lives on, using
/// <see cref="VirtualDesktopProvider.GetDesktopIndexForHwnd"/>.
///
/// mstsc window titles typically look like "MACHINENAME - Remote Desktop Connection"
/// so we match by checking whether the title contains a known peer machine name.
///
/// msrdc window titles are the DevBox/workspace friendly name (e.g. "davris-10")
/// without a separator.  <see cref="GetMsrdcDesktopMap"/> returns all msrdc windows
/// without name filtering.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>
    /// Returns a mapping from machine name → 1-based virtual desktop index for every
    /// visible mstsc.exe or msrdc.exe window whose title contains a known peer machine name.
    /// Returns an empty dictionary if the VirtualDesktop API is unavailable.
    /// </summary>
    public static Dictionary<string, int> GetRdpDesktopMap(IEnumerable<string> knownMachineNames)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!VirtualDesktopProvider.IsAvailable)
        {
            AppLogger.Log("RdpWindowLocator: VirtualDesktopProvider unavailable, returning empty map");
            return result;
        }

        // Build lowercase lookup list so we can match case-insensitively inside window titles.
        var names = new List<string>(knownMachineNames);
        if (names.Count == 0) return result;

        var rdpPids  = GetRdpClientProcessIds();
        AppLogger.Log($"RdpWindowLocator: found {rdpPids.Count} mstsc PIDs, looking for [{string.Join(", ", names)}]");
        var titleBuf   = new StringBuilder(512);
        var unmatchedTitles = new List<string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!rdpPids.Contains(pid)) return true;

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
                else
                    AppLogger.Log($"RdpWindowLocator: matched '{title}' → {matchedName} but GetDesktopIndexForHwnd returned 0");
            }
            else
            {
                unmatchedTitles.Add(title);
            }
            return true;
        }, IntPtr.Zero);

        if (unmatchedTitles.Count > 0)
            AppLogger.Log($"RdpWindowLocator: {unmatchedTitles.Count} mstsc window(s) didn't match known names: [{string.Join(", ", unmatchedTitles)}]");
        AppLogger.Log($"RdpWindowLocator: mstsc result = {{{string.Join(", ", result.Select(kv => $"{kv.Key}→{kv.Value}"))}}}");

        return result;
    }

    /// <summary>
    /// Returns a mapping from window title → 1-based virtual desktop index for every
    /// visible msrdc.exe window with the <c>TscShellContainerClass</c> window class.
    /// Unlike <see cref="GetRdpDesktopMap"/>, this does not require a list of known
    /// names — it returns all msrdc windows, allowing the caller to pair them with
    /// DevBox/AVD peers identified via <see cref="RdpConnectionDetector.GetRdpClientName"/>.
    /// </summary>
    public static Dictionary<string, int> GetMsrdcDesktopMap()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!VirtualDesktopProvider.IsAvailable) return result;

        var msrdcPids = GetProcessIds("msrdc");
        if (msrdcPids.Count == 0) return result;

        var titleBuf = new StringBuilder(512);
        var classBuf = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!msrdcPids.Contains(pid)) return true;

            classBuf.Clear();
            GetClassNameW(hwnd, classBuf, classBuf.Capacity);
            if (!classBuf.ToString().Equals("TscShellContainerClass", StringComparison.Ordinal))
                return true;

            titleBuf.Clear();
            GetWindowTextW(hwnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();
            if (string.IsNullOrEmpty(title)) return true;

            var idx = VirtualDesktopProvider.GetDesktopIndexForHwnd(hwnd);
            if (idx > 0)
                result[title] = idx;
            else
                AppLogger.Log($"RdpWindowLocator: msrdc window '{title}' GetDesktopIndexForHwnd returned 0");

            return true;
        }, IntPtr.Zero);

        AppLogger.Log($"RdpWindowLocator: GetMsrdcDesktopMap found {result.Count} window(s): [{string.Join(", ", result.Select(kv => $"'{kv.Key}'→desktop{kv.Value}"))}]");
        return result;
    }

    /// <summary>
    /// Returns PIDs for mstsc.exe only.  msrdc.exe (Cloud DevBox / AVD) windows
    /// lack the "HOSTNAME - Remote Desktop Connection" title format that
    /// <see cref="MatchMachineName"/> requires, so they are handled separately
    /// by <see cref="GetMsrdcDesktopMap"/>.
    /// </summary>
    private static HashSet<uint> GetRdpClientProcessIds() => GetProcessIds("mstsc");

    /// <summary>
    /// Scans msrdc.exe (Windows App) windows and matches their titles against known
    /// peer machine names.  This handles the case where the user connects via Windows
    /// App instead of mstsc.exe — the window title is typically the machine hostname
    /// or a friendly name that may contain the hostname.
    /// </summary>
    public static Dictionary<string, int> GetMsrdcDesktopMapByName(IEnumerable<string> knownMachineNames)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!VirtualDesktopProvider.IsAvailable) return result;

        var names = new List<string>(knownMachineNames);
        if (names.Count == 0) return result;

        var msrdcPids = GetProcessIds("msrdc");
        if (msrdcPids.Count == 0) return result;

        var titleBuf = new StringBuilder(512);
        var classBuf = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!msrdcPids.Contains(pid)) return true;

            classBuf.Clear();
            GetClassNameW(hwnd, classBuf, classBuf.Capacity);
            if (!classBuf.ToString().Equals("TscShellContainerClass", StringComparison.Ordinal))
                return true;

            titleBuf.Clear();
            GetWindowTextW(hwnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();
            if (string.IsNullOrEmpty(title)) return true;

            // Try matching the title against known names.  Windows App titles may be:
            // - "HOSTNAME" (exact machine name)
            // - "HOSTNAME - Remote Desktop" (similar to mstsc)
            // - A friendly name that contains the hostname
            var matched = MatchMsrdcTitle(title, names);
            if (matched != null)
            {
                var idx = VirtualDesktopProvider.GetDesktopIndexForHwnd(hwnd);
                if (idx > 0)
                    result[matched] = idx;
            }
            return true;
        }, IntPtr.Zero);

        AppLogger.Log($"RdpWindowLocator: msrdc name-match result = {{{string.Join(", ", result.Select(kv => $"{kv.Key}→{kv.Value}"))}}}");
        return result;
    }

    /// <summary>
    /// Matches an msrdc window title against known machine names.
    /// Tries multiple strategies: exact match, "HOSTNAME - ..." prefix match,
    /// and substring containment.
    /// </summary>
    internal static string? MatchMsrdcTitle(string windowTitle, IEnumerable<string> names)
    {
        var normalizedTitle = MachineInfo.NormalizeHostname(windowTitle);

        foreach (var name in names)
        {
            var normalizedName = MachineInfo.NormalizeHostname(name);
            if (string.IsNullOrEmpty(normalizedName)) continue;

            // Exact match (title IS the machine name).
            if (normalizedTitle.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // Try " - " prefix extraction (like mstsc format).
        var matched = MatchMachineName(windowTitle, names);
        if (matched != null) return matched;

        // Substring: title contains the machine name at a word boundary.
        // Must not be followed or preceded by an alphanumeric char to avoid
        // false positives like "DAVRIS-1" matching inside "davris-10".
        foreach (var name in names)
        {
            var normalizedName = MachineInfo.NormalizeHostname(name);
            if (string.IsNullOrEmpty(normalizedName)) continue;

            var idx = windowTitle.IndexOf(normalizedName, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var end = idx + normalizedName.Length;
                var prefixOk = idx == 0 || !char.IsLetterOrDigit(windowTitle[idx - 1]);
                var suffixOk = end >= windowTitle.Length || !char.IsLetterOrDigit(windowTitle[end]);
                if (prefixOk && suffixOk)
                    return name;
                idx = windowTitle.IndexOf(normalizedName, end, StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    private static HashSet<uint> GetProcessIds(string processName)
    {
        var set = new HashSet<uint>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            try { set.Add((uint)p.Id); }
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>
    /// Returns the first name from <paramref name="names"/> whose short hostname
    /// (first DNS label) matches the hostname portion of <paramref name="windowTitle"/>,
    /// or <see langword="null"/> if none match.
    /// mstsc window titles are "HOSTNAME - Remote Desktop Connection", where HOSTNAME
    /// may be a short name, FQDN, or IP address.  Both sides are normalized to their
    /// first DNS label before comparison so that "davris-0.corp.com" matches "DAVRIS-0".
    /// Extracted for unit testing without a real window enumeration.
    /// </summary>
    internal static string? MatchMachineName(string windowTitle, IEnumerable<string> names)
    {
        // Extract the hostname portion: everything before the first " - " separator.
        const string sep = " - ";
        var sepIdx = windowTitle.IndexOf(sep, StringComparison.Ordinal);
        if (sepIdx <= 0) return null;
        var titleHost = MachineInfo.NormalizeHostname(windowTitle[..sepIdx]);
        if (string.IsNullOrEmpty(titleHost)) return null;

        foreach (var name in names)
        {
            if (MachineInfo.NormalizeHostname(name).Equals(titleHost, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }
}
