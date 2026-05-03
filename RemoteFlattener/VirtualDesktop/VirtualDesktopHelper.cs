using System;
using System.IO;
using System.Security;
using Microsoft.Win32;
using WindowsDesktop;

namespace RemoteFlattener.VirtualDesktop;

/// <summary>
/// Reads virtual desktop state from the Windows registry.
/// Works without undocumented COM interfaces by reading the Explorer VirtualDesktops key.
/// </summary>
public static class VirtualDesktopHelper
{
    private const string RegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>Returns the GUID of the currently active virtual desktop.
    /// Uses the VirtualDesktop API if available, otherwise reads the registry.</summary>
    public static Guid GetCurrentDesktopGuid()
    {
        // If the provider is available, use it to get a reliable GUID.
        if (VirtualDesktopProvider.IsAvailable)
        {
            try
            {
                return WindowsDesktop.VirtualDesktop.Current.Id;
            }
            catch { /* fall through to registry */ }
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] data && data.Length >= 16)
                return new Guid(data[..16]);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) { }
        return Guid.Empty;
    }

    /// <summary>Returns the total number of virtual desktops (1 if only one exists).
    /// Uses the VirtualDesktop API if available, otherwise reads the registry.</summary>
    public static int GetTotalDesktopCount()
    {
        if (VirtualDesktopProvider.IsAvailable)
        {
            try { return WindowsDesktop.VirtualDesktop.GetDesktops().Length; }
            catch { /* fall through to registry */ }
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue("VirtualDesktopIDs") is byte[] data && data.Length >= 16)
                return data.Length / 16;
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) { }
        return 1;
    }

    /// <summary>Returns the 1-based index of the current virtual desktop.
    /// Uses the VirtualDesktop API if available, otherwise reads the registry.</summary>
    public static int GetCurrentDesktopIndex()
    {
        if (VirtualDesktopProvider.IsAvailable)
        {
            var (index, _) = VirtualDesktopProvider.GetDesktopState();
            if (index > 0) return index;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null) return 1;

            var currentData = key.GetValue("CurrentVirtualDesktop") as byte[];
            var allData = key.GetValue("VirtualDesktopIDs") as byte[];
            if (currentData == null || currentData.Length < 16 || allData == null) return 1;

            var currentGuid = new Guid(currentData[..16]);
            int count = allData.Length / 16;
            for (int i = 0; i < count; i++)
            {
                var guid = new Guid(allData[(i * 16)..((i + 1) * 16)]);
                if (guid == currentGuid) return i + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) { }
        return 1;
    }
}
