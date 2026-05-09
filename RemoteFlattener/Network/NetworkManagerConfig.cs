using RemoteFlattener.Models;

namespace RemoteFlattener.Network;

/// <summary>
/// Configuration for <see cref="NetworkManager"/>.
/// All properties have sensible defaults for production; override them for testing.
/// </summary>
public sealed record NetworkManagerConfig
{
    /// <summary>TCP port for listening and outgoing connections. Default: 8765.</summary>
    public int Port { get; init; } = 8765;

    /// <summary>
    /// The machine name this node will identify as.
    /// Default: the local machine's normalized hostname.
    /// </summary>
    public string LocalMachineName { get; init; } = MachineInfo.NormalizeHostname(Environment.MachineName);

    /// <summary>Logger used for diagnostic output. Default: <see cref="AppLoggerAdapter"/>.</summary>
    public INetworkLogger Logger { get; init; } = AppLoggerAdapter.Instance;
}
