using RemoteFlattener.Models;

namespace RemoteFlattener.Network;

/// <summary>
/// The exact host and port used to establish a network connection. The host is not
/// canonicalized because DNS may require an FQDN or a specific IP address.
/// </summary>
internal readonly record struct MachineEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// Associates the name used for peer identity with the exact endpoint used to reach it.
/// </summary>
internal readonly record struct PeerTarget(MachineName Name, MachineEndpoint Endpoint);
