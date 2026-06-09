using System;
using System.Diagnostics;

namespace RemoteFlattener.Logging;

/// <summary>
/// Lightweight performance tracing utility. Creates a named trace scope with
/// automatic elapsed-time logging. Supports checkpoints within a scope.
///
/// Usage:
///   using var perf = PerfTrace.Begin("Win+Tab open");
///   // ... do work ...
///   perf.Checkpoint("TreeWindow constructed");
///   // ... more work ...
///   // Dispose logs total elapsed time automatically.
///
/// All output goes through <see cref="AppLogger"/> with a "[PERF]" prefix so
/// it can be easily grep'd from the log file.
/// </summary>
public sealed class PerfTrace : IDisposable
{
    private readonly string _name;
    private readonly Stopwatch _stopwatch;
    private bool _disposed;

    private PerfTrace(string name)
    {
        _name = name;
        _stopwatch = Stopwatch.StartNew();
        AppLogger.Log($"[PERF] ▶ {_name} — started");
    }

    /// <summary>Begins a new named performance trace.</summary>
    public static PerfTrace Begin(string name) => new(name);

    /// <summary>
    /// Logs an intermediate checkpoint within this trace scope.
    /// </summary>
    public void Checkpoint(string label)
    {
        if (_disposed) return;
        AppLogger.Log($"[PERF]   {_name} | {label} @ {_stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Logs the total elapsed time for this trace scope.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopwatch.Stop();
        AppLogger.Log($"[PERF] ■ {_name} — completed in {_stopwatch.ElapsedMilliseconds} ms");
    }
}
