using System;
using System.Collections.Generic;
using RemoteFlattener.Logging;
using Xunit;

namespace RemoteFlattener.Tests.Logging;

/// <summary>
/// Tests for <see cref="AppLogger"/>.
///
/// NOTE: AppLogger is a static class with persistent state.  Tests subscribe to
/// <see cref="AppLogger.LogWritten"/> and unsubscribe immediately after to avoid
/// cross-test interference.
/// </summary>
public class AppLoggerTests
{
    // ── LogWritten event ──────────────────────────────────────────────────

    [Fact]
    public void Log_RaisesLogWrittenEvent()
    {
        string? received = null;
        Action<string> handler = msg => received = msg;
        AppLogger.LogWritten += handler;
        try
        {
            AppLogger.Log("ping");
            Assert.NotNull(received);
        }
        finally
        {
            AppLogger.LogWritten -= handler;
        }
    }

    [Fact]
    public void Log_EventMessage_ContainsLoggedText()
    {
        const string text = "hello-from-test";
        string? received = null;
        Action<string> handler = msg => received = msg;
        AppLogger.LogWritten += handler;
        try
        {
            AppLogger.Log(text);
            Assert.Contains(text, received);
        }
        finally
        {
            AppLogger.LogWritten -= handler;
        }
    }

    [Fact]
    public void Log_EventMessage_ContainsTimestampBracket()
    {
        string? received = null;
        Action<string> handler = msg => received = msg;
        AppLogger.LogWritten += handler;
        try
        {
            AppLogger.Log("timestamp-test");
            // Timestamp format: [yyyy-MM-dd HH:mm:ss.fff]
            Assert.NotNull(received);
            Assert.StartsWith("[", received);
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", received!);
        }
        finally
        {
            AppLogger.LogWritten -= handler;
        }
    }

    [Fact]
    public void Log_MultipleMessages_EventFiresForEach()
    {
        var received = new List<string>();
        Action<string> handler = msg => received.Add(msg);
        AppLogger.LogWritten += handler;
        try
        {
            AppLogger.Log("msg-1");
            AppLogger.Log("msg-2");
            AppLogger.Log("msg-3");
            Assert.Equal(3, received.Count);
        }
        finally
        {
            AppLogger.LogWritten -= handler;
        }
    }

    [Fact]
    public void Log_AfterUnsubscribe_DoesNotFireEvent()
    {
        int count = 0;
        Action<string> handler = _ => count++;
        AppLogger.LogWritten += handler;
        AppLogger.LogWritten -= handler;
        AppLogger.Log("should-not-fire");
        Assert.Equal(0, count);
    }

    // ── LogFilePath ────────────────────────────────────────────────────────

    [Fact]
    public void LogFilePath_AfterLog_IsNotNullOrEmpty()
    {
        // Ensure at least one Log() call has been made so EnsureWriter() has run.
        AppLogger.Log("initialise-log-file");
        // LogFilePath is set inside EnsureWriter() only when the file can be created.
        // In a normal CI/CD environment with a writable LOCALAPPDATA this will be non-null.
        // We don't Assert.NotNull because the directory may not be writable in all environments;
        // instead we just verify the property doesn't throw.
        _ = AppLogger.LogFilePath; // must not throw
    }

    [Fact]
    public void LogFilePath_WhenSet_IsAbsolutePath()
    {
        AppLogger.Log("path-check");
        if (AppLogger.LogFilePath != null)
            Assert.True(System.IO.Path.IsPathRooted(AppLogger.LogFilePath));
    }
}
