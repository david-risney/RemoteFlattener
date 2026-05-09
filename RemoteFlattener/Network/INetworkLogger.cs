namespace RemoteFlattener.Network;

/// <summary>
/// Abstraction over logging so that <see cref="NetworkManager"/> can be tested
/// without depending on the static <see cref="Logging.AppLogger"/> and its file I/O.
/// </summary>
public interface INetworkLogger
{
    void Log(string message);
}
