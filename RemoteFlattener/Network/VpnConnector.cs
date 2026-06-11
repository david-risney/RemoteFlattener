using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemoteFlattener.Logging;

namespace RemoteFlattener.Network;

/// <summary>
/// Manages VPN connections using rasdial.exe and the RAS phonebook for enumeration.
/// </summary>
public static class VpnConnector
{
    private static readonly string PhonebookPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Network\Connections\Pbk\rasphone.pbk");

    /// <summary>
    /// Returns the names of all user-scoped VPN connections configured on this machine.
    /// Reads the RAS phonebook file directly (no process spawn).
    /// </summary>
    public static List<string> GetAvailableVpnConnections()
    {
        try
        {
            if (!File.Exists(PhonebookPath))
                return new List<string>();

            return File.ReadLines(PhonebookPath)
                .Where(line => line.StartsWith('[') && line.EndsWith(']'))
                .Select(line => line[1..^1])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"VPN: Failed to enumerate connections: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Ensures the named VPN is connected. Handles stuck "Connecting" states by
    /// forcing a disconnect/reconnect cycle. Returns true on success.
    /// </summary>
    public static async Task<bool> ConnectAsync(string connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            return false;

        var status = await GetConnectionStatusAsync(connectionName);

        if (string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase))
            return true;

        // If stuck in "Connecting", force disconnect first to get a clean state.
        if (string.Equals(status, "Connecting", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log($"VPN: '{connectionName}' is stuck in Connecting state — forcing disconnect.");
            await DisconnectAsync(connectionName);
            await Task.Delay(2_000);
        }

        AppLogger.Log($"VPN: Connecting to '{connectionName}'...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rasdial.exe",
                Arguments = $"\"{connectionName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                AppLogger.Log("VPN: Failed to start rasdial.exe.");
                return false;
            }

            // Timeout after 30 seconds to avoid hanging on VPNs that require interactive auth.
            using var cts = new CancellationTokenSource(30_000);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                AppLogger.Log($"VPN: Connection to '{connectionName}' timed out (may require interactive authentication).");
                return false;
            }

            if (process.ExitCode == 0)
            {
                // Verify the connection actually reached "Connected" state.
                await Task.Delay(3_000);
                var postStatus = await GetConnectionStatusAsync(connectionName);
                if (string.Equals(postStatus, "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"VPN: Successfully connected to '{connectionName}'.");
                    return true;
                }

                AppLogger.Log($"VPN: rasdial returned success but status is '{postStatus}' — connection may be unstable.");
                return false;
            }

            var error = await process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            AppLogger.Log($"VPN: rasdial failed (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"VPN: Error connecting: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the connection status via Get-VpnConnection (accurate for plugin VPNs).
    /// Returns "Connected", "Connecting", "Disconnected", or null on error.
    /// </summary>
    private static async Task<string?> GetConnectionStatusAsync(string connectionName)
    {
        try
        {
            var output = await RunProcessAsync("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(Get-VpnConnection -Name '{connectionName.Replace("'", "''")}').ConnectionStatus\"",
                timeoutMs: 10_000);
            return output?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Disconnects the named VPN via rasdial /disconnect.
    /// </summary>
    private static async Task DisconnectAsync(string connectionName)
    {
        try
        {
            var output = await RunProcessAsync("rasdial.exe",
                $"\"{connectionName}\" /disconnect",
                timeoutMs: 10_000);
        }
        catch { }
    }

    private static async Task<string?> RunProcessAsync(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return null;
        }

        return process.ExitCode == 0 ? await process.StandardOutput.ReadToEndAsync() : null;
    }
}
