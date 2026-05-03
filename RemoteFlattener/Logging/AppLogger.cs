using System;
using System.IO;

namespace RemoteFlattener.Logging;

/// <summary>
/// Simple thread-safe logger.  Writes timestamped lines to a per-session log file under
/// %LOCALAPPDATA%\RemoteFlattener\ and raises <see cref="LogWritten"/> so the UI can
/// display live entries.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;

    /// <summary>Full path of the current log file.  Set on first call to <see cref="Log"/>.</summary>
    public static string? LogFilePath { get; private set; }

    /// <summary>Raised on the calling thread whenever a line is logged.</summary>
    public static event Action<string>? LogWritten;

    public static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] {message}";

        lock (_lock)
        {
            EnsureWriter();
            try
            {
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch { /* don't let logging break the app */ }
        }

        // Raise outside the lock so subscribers don't deadlock.
        try { LogWritten?.Invoke(line); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EnsureWriter()
    {
        if (_writer != null) return;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteFlattener");
            Directory.CreateDirectory(dir);

            var filename = $"RemoteFlattener_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            LogFilePath = Path.Combine(dir, filename);
            _writer = new StreamWriter(LogFilePath, append: false, System.Text.Encoding.UTF8)
            {
                AutoFlush = false
            };
        }
        catch { /* if we can't open the file, log events still fire */ }
    }
}
