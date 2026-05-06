using System;
using System.IO;

namespace RemoteFlattener;

/// <summary>Centralizes all file-system paths used by the application.</summary>
public static class AppPaths
{
    /// <summary>Per-user data directory: %LOCALAPPDATA%\RemoteFlattener</summary>
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteFlattener");
}
