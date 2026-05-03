using System;
using System.IO;
using Microsoft.Win32;
using RemoteFlattener.Logging;
using WindowsDesktop;

namespace RemoteFlattener.VirtualDesktop;

/// <summary>
/// Thin, fault-tolerant wrapper around the Grabacr07/VirtualDesktop library.
///
/// The library uses private COM interfaces whose IIDs change between Windows
/// builds.  Every public method on this class is wrapped in a try/catch so that
/// any <see cref="Exception"/> (typically <see cref="System.Runtime.InteropServices.COMException"/>
/// or <see cref="InvalidCastException"/>) simply marks the provider unavailable
/// and lets callers fall back to the registry / SendInput approaches.
///
/// Call <see cref="TryInitialize"/> once from the UI thread before using the other members.
/// </summary>
public static class VirtualDesktopProvider
{
    private static bool _initialized;
    private static bool _available;

    /// <summary>True when the library was initialised successfully on this machine/Windows build.</summary>
    public static bool IsAvailable => _available;

    /// <summary>
    /// Raised (on a background thread) whenever the active virtual desktop changes.
    /// Only fires when <see cref="IsAvailable"/> is true.
    /// </summary>
    public static event Action? DesktopChanged;

    /// <summary>
    /// Attempts to initialise the COM interop layer.  Safe to call multiple times;
    /// after the first call the result is cached.
    /// </summary>
    public static bool TryInitialize()
    {
        if (_initialized) return _available;
        _initialized = true;

        try
        {
            var desktops = WindowsDesktop.VirtualDesktop.GetDesktops();
            _available = desktops is { Length: > 0 };

            if (_available)
            {
                WindowsDesktop.VirtualDesktop.CurrentChanged += OnCurrentChanged;
                AppLogger.Log($"VirtualDesktop API available — {desktops!.Length} desktop(s) detected.");
            }
        }
        catch (Exception ex)
        {
            _available = false;
            AppLogger.Log($"VirtualDesktop API unavailable ({ex.GetType().Name}: {ex.Message}). " +
                          "Falling back to registry/SendInput.");
        }

        return _available;
    }

    // ── State queries ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (1-based current desktop index, total desktop count).
    /// Returns (0, 0) if the API is unavailable or throws.
    /// </summary>
    public static (int Index, int Count) GetDesktopState()
    {
        if (!_available) return (0, 0);
        try
        {
            var desktops = WindowsDesktop.VirtualDesktop.GetDesktops();
            var current  = WindowsDesktop.VirtualDesktop.Current;
            for (int i = 0; i < desktops.Length; i++)
                if (desktops[i].Id == current.Id)
                    return (i + 1, desktops.Length);
            return (0, desktops.Length);
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return (0, 0);
        }
    }

    /// <summary>Information about a single virtual desktop.</summary>
    public sealed record DesktopInfo(int Index, string DisplayName, bool IsCurrent, Guid Id, string? WallpaperPath);

    /// <summary>
    /// Returns all virtual desktops with their 1-based index, display name, and whether
    /// each is currently active.  Returns an empty array if the API is unavailable or throws.
    /// </summary>
    public static DesktopInfo[] GetAllDesktops()
    {
        if (!_available) return Array.Empty<DesktopInfo>();
        try
        {
            var desktops  = WindowsDesktop.VirtualDesktop.GetDesktops();
            var currentId = WindowsDesktop.VirtualDesktop.Current.Id;

            // Wallpaper fallback chain (per-desktop path may be empty, registry blank in RDP sessions):
            // 1. Registry Control Panel\Desktop\Wallpaper
            // 2. TranscodedWallpaper — Windows always keeps this file up to date, even in RDP.
            string? systemWall = null;
            try
            {
                var p = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", null) as string;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) systemWall = p;
            }
            catch { }

            if (systemWall == null)
            {
                var transcoded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (File.Exists(transcoded)) systemWall = transcoded;
            }

            var result = new DesktopInfo[desktops.Length];
            for (int i = 0; i < desktops.Length; i++)
            {
                var d    = desktops[i];
                var name = string.IsNullOrWhiteSpace(d.Name) ? $"Desktop {i + 1}" : d.Name;
                var wall = string.IsNullOrEmpty(d.WallpaperPath) ? systemWall : d.WallpaperPath;
                result[i] = new DesktopInfo(i + 1, name, d.Id == currentId, d.Id, wall);
            }
            return result;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return Array.Empty<DesktopInfo>();
        }
    }

    // ── Switching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches directly to the desktop with the given <paramref name="id"/>.
    /// Returns true on success.
    /// </summary>
    public static bool SwitchToDesktop(Guid id)
    {
        if (!_available) return false;
        try
        {
            var desktop = WindowsDesktop.VirtualDesktop.FromId(id);
            if (desktop == null) return false;
            desktop.Switch();
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return false;
        }
    }

    /// <summary>
    /// Switches to the desktop at the given 1-based <paramref name="index"/>.
    /// Returns true on success.
    /// </summary>
    public static bool SwitchToIndex(int index)
    {
        if (!_available) return false;
        try
        {
            var desktops = WindowsDesktop.VirtualDesktop.GetDesktops();
            if (index < 1 || index > desktops.Length) return false;
            desktops[index - 1].Switch();
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return false;
        }
    }

    /// <summary>
    /// Switches to the desktop immediately to the left of the current one.
    /// Returns true on success, false if there is no desktop to the left or the API failed.
    /// </summary>
    public static bool SwitchLeft()
    {
        if (!_available) return false;
        try
        {
            var left = WindowsDesktop.VirtualDesktop.Current.GetLeft();
            if (left == null) return false;
            left.Switch();
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return false;
        }
    }

    /// <summary>
    /// Switches to the desktop immediately to the right of the current one.
    /// Returns true on success, false if there is no desktop to the right or the API failed.
    /// </summary>
    public static bool SwitchRight()
    {
        if (!_available) return false;
        try
        {
            var right = WindowsDesktop.VirtualDesktop.Current.GetRight();
            if (right == null) return false;
            right.Switch();
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return false;
        }
    }

    // ── Window → desktop queries ──────────────────────────────────────────────

    /// <summary>
    /// Returns the 1-based index of the virtual desktop that hosts <paramref name="hwnd"/>,
    /// or 0 if the API is unavailable, the window is pinned/not found, or an error occurs.
    /// </summary>
    public static int GetDesktopIndexForHwnd(IntPtr hwnd)
    {
        if (!_available || hwnd == IntPtr.Zero) return 0;
        try
        {
            var target   = WindowsDesktop.VirtualDesktop.FromHwnd(hwnd);
            if (target == null) return 0;
            var desktops = WindowsDesktop.VirtualDesktop.GetDesktops();
            for (int i = 0; i < desktops.Length; i++)
                if (desktops[i].Id == target.Id) return i + 1;
            return 0;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return 0;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void OnCurrentChanged(object? sender, VirtualDesktopChangedEventArgs e)
    {
        try { DesktopChanged?.Invoke(); }
        catch { /* don't let a subscriber crash the COM event thread */ }
    }

    private static void MarkUnavailable(Exception ex)
    {
        if (!_available) return;
        _available = false;
        try
        {
            WindowsDesktop.VirtualDesktop.CurrentChanged -= OnCurrentChanged;
        }
        catch { }
        AppLogger.Log($"VirtualDesktop API failed mid-session ({ex.GetType().Name}: {ex.Message}). " +
                      "Falling back to registry/SendInput for remainder of session.");
    }
}
