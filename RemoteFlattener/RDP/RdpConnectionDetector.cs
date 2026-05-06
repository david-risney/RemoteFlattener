using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using RemoteFlattener.Models;

namespace RemoteFlattener.RDP;

/// <summary>
/// A discovered RDP peer: the short machine name (used for display and dedup)
/// and the IP address string to use as the TCP connection target.
/// </summary>
public record RdpPeer(string MachineName, string ConnectionAddress);

/// <summary>
/// Discovers the remote hosts of active RDP connections by inspecting TCP port 3389.
/// On an RDP server the client's address appears as the remote end of an established
/// inbound connection on port 3389.  On an RDP client the server's address appears as
/// the remote end of an outbound connection to port 3389.
/// </summary>
public static class RdpConnectionDetector
{
    private const int RdpPort = 3389;

    /// <summary>
    /// Returns each active RDP peer as an <see cref="RdpPeer"/> containing both the
    /// resolved short machine name (for display/dedup) and the raw IP address string
    /// (for the TCP connection — guaranteed reachable because the RDP session uses it).
    /// </summary>
    public static IReadOnlyList<RdpPeer> GetRdpPeers()
    {
        var localHostname = Environment.MachineName;
        var addresses = ExtractRdpAddresses(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections());
        return ResolveToRdpPeers(addresses, localHostname,
            addr => Dns.GetHostEntry(addr).HostName);
    }

    /// <summary>
    /// Returns the hostnames (or IP strings if reverse-DNS fails) of all machines
    /// currently connected via RDP, excluding loopback and the local machine itself.
    /// </summary>
    public static IReadOnlyList<string> GetRdpPeerHostnames() =>
        GetRdpPeers().Select(p => p.MachineName).ToList();

    /// <summary>
    /// Returns the unique remote IP addresses of all established TCP connections
    /// where either endpoint is on port <see cref="RdpPort"/>.
    /// Extracted for unit testing without live network state.
    /// </summary>
    internal static IEnumerable<IPAddress> ExtractRdpAddresses(
        IEnumerable<TcpConnectionInformation> connections)
    {
        var seen = new HashSet<IPAddress>();
        foreach (var conn in connections)
        {
            if (conn.State != TcpState.Established) continue;
            if (conn.LocalEndPoint.Port  == RdpPort) seen.Add(conn.RemoteEndPoint.Address);
            else if (conn.RemoteEndPoint.Port == RdpPort) seen.Add(conn.RemoteEndPoint.Address);
        }
        return seen;
    }

    /// <summary>
    /// Resolves each IP to a <see cref="RdpPeer"/> with the short hostname for display/dedup
    /// and the raw IP string as the connection address.  Falls back to IP-as-name when
    /// <paramref name="resolveHostname"/> throws.
    /// Extracted for unit testing with a fake resolver.
    /// </summary>
    internal static IReadOnlyList<RdpPeer> ResolveToRdpPeers(
        IEnumerable<IPAddress> addresses,
        string localHostname,
        Func<IPAddress, string> resolveHostname)
    {
        var results = new List<RdpPeer>();
        foreach (var addr in addresses)
        {
            if (IPAddress.IsLoopback(addr)) continue;

            string hostname;
            try
            {
                hostname = MachineInfo.NormalizeHostname(resolveHostname(addr));
            }
            catch
            {
                hostname = addr.ToString();
            }

            if (!hostname.Equals(localHostname, StringComparison.OrdinalIgnoreCase))
                results.Add(new RdpPeer(hostname, addr.ToString()));
        }
        return results;
    }

    /// <summary>
    /// Resolves each IP to a short hostname via <paramref name="resolveHostname"/>,
    /// then filters out loopback and the local machine.
    /// Falls back to the raw IP string if <paramref name="resolveHostname"/> throws.
    /// Extracted for unit testing with a fake resolver.
    /// </summary>
    internal static IReadOnlyList<string> ResolveToHostnames(
        IEnumerable<IPAddress> addresses,
        string localHostname,
        Func<IPAddress, string> resolveHostname)
    {
        var results = new List<string>();
        foreach (var addr in addresses)
        {
            if (IPAddress.IsLoopback(addr)) continue;

            string hostname;
            try
            {
                // NormalizeHostname strips the FQDN to just the short machine name
                // so it matches what Environment.MachineName returns on the peer.
                hostname = MachineInfo.NormalizeHostname(resolveHostname(addr));
            }
            catch
            {
                hostname = addr.ToString();
            }

            if (!hostname.Equals(localHostname, StringComparison.OrdinalIgnoreCase))
                results.Add(hostname);
        }
        return results;
    }
}
