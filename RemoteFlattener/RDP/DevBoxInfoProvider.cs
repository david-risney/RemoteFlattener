using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemoteFlattener.Logging;

namespace RemoteFlattener.RDP;

/// <summary>
/// Discovers the Cloud DevBox friendly name from the DevBox Agent configuration.
/// The friendly name is the last colon-separated segment of
/// <c>DevBoxAgent.metadata.devBoxDataplaneId</c> in <c>appsettings.Production.json</c>.
/// </summary>
public static class DevBoxInfoProvider
{
    private static readonly string DefaultAgentRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     "Microsoft Dev Box Agent");

    private static string? _cachedFriendlyName;
    private static bool _cacheValid;

    /// <summary>
    /// Returns the DevBox friendly name, or null if not running on a DevBox
    /// or the name cannot be determined. Caches successful results.
    /// </summary>
    public static string? GetDevBoxFriendlyName()
    {
        return GetDevBoxFriendlyName(DefaultAgentRoot, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// Testable overload accepting custom agent root and env reader.
    /// </summary>
    internal static string? GetDevBoxFriendlyName(string agentRoot, Func<string, string?> getEnv)
    {
        if (_cacheValid) return _cachedFriendlyName;

        var isDevBox = getEnv("IsDevBox");
        if (!string.Equals(isDevBox, "True", StringComparison.OrdinalIgnoreCase))
            return null; // Not a DevBox — don't cache, env could change on reconnect

        var name = ReadFriendlyNameFromConfig(agentRoot);
        if (name != null)
        {
            _cachedFriendlyName = name;
            _cacheValid = true;
        }
        return name;
    }

    /// <summary>Resets the cache (for testing).</summary>
    internal static void ResetCache()
    {
        _cachedFriendlyName = null;
        _cacheValid = false;
    }

    private static string? ReadFriendlyNameFromConfig(string agentRoot)
    {
        try
        {
            if (!Directory.Exists(agentRoot)) return null;

            foreach (var file in Directory.EnumerateFiles(agentRoot, "appsettings.Production.json", SearchOption.AllDirectories))
            {
                var name = ExtractFriendlyName(file);
                if (name != null)
                {
                    AppLogger.Log($"DevBoxInfoProvider: found friendly name '{name}' from '{file}'");
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"DevBoxInfoProvider: error scanning agent root '{agentRoot}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Extracts the friendly name from a single appsettings.Production.json file.
    /// Returns null if the file is malformed or doesn't contain the expected field.
    /// </summary>
    internal static string? ExtractFriendlyName(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("DevBoxAgent", out var agentElement))
                return null;
            if (!agentElement.TryGetProperty("metadata", out var metadataElement))
                return null;
            if (!metadataElement.TryGetProperty("devBoxDataplaneId", out var dataplaneId))
                return null;

            var value = dataplaneId.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;

            // Format: <tenantId>:<devCenterName>:<projectName>:<poolId>:<friendlyName>
            var lastColon = value.LastIndexOf(':');
            if (lastColon < 0 || lastColon == value.Length - 1) return null;

            var name = value[(lastColon + 1)..].Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"DevBoxInfoProvider: failed to parse '{filePath}': {ex.Message}");
            return null;
        }
    }
}
