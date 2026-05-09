namespace RemoteFlattener.Network;

/// <summary>Silently discards all log messages. Useful in tests.</summary>
public sealed class NullNetworkLogger : INetworkLogger
{
    public static NullNetworkLogger Instance { get; } = new();
    public void Log(string message) { }
}
