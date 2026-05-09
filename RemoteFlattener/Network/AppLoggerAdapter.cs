using RemoteFlattener.Logging;

namespace RemoteFlattener.Network;

/// <summary>Delegates to the static <see cref="AppLogger"/> for production use.</summary>
public sealed class AppLoggerAdapter : INetworkLogger
{
    public static AppLoggerAdapter Instance { get; } = new();
    public void Log(string message) => AppLogger.Log(message);
}
