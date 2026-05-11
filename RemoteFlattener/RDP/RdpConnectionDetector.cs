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
/// Falls back to the <c>CLIENTNAME</c> environment variable when no port-3389
/// connections are found (e.g. Cloud DevBox / AVD with WebRTC transport).
/// </summary>
public static class RdpConnectionDetector
{
    private const int RdpPort = 3389;

    /// <summary>
    /// Returns each active RDP peer as an <see cref="RdpPeer"/> containing both the
    /// resolved short machine name (for display/dedup) and the raw IP address string
    /// (for the TCP connection — guaranteed reachable because the RDP session uses it).
    /// When no port-3389 peers are found on a remote session (e.g. Cloud DevBox),
    /// falls back to the <c>CLIENTNAME</c> environment variable.
    /// </summary>
    public static IReadOnlyList<RdpPeer> GetRdpPeers()
    {
        var localHostname = Environment.MachineName;
        var addresses = ExtractRdpAddresses(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections());
        var peers = ResolveToRdpPeers(addresses, localHostname,
            addr => Dns.GetHostEntry(addr).HostName);

        if (peers.Count == 0)
        {
            var fallback = GetClientNameFallbackPeers(localHostname);
            if (fallback.Count > 0) return fallback;
        }

        return peers;
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

    /// <summary>
    /// Returns the value of the <c>CLIENTNAME</c> environment variable (normalized)
    /// when running inside a remote session, or <see langword="null"/> otherwise.
    /// On Cloud DevBox / AVD sessions that use WebRTC transport, this is the only
    /// way to discover the RDP client machine name.
    /// </summary>
    public static string? GetRdpClientName()
    {
        if (!RdpRoleDetector.IsRemoteSession()) return null;
        var clientName = Environment.GetEnvironmentVariable("CLIENTNAME");
        if (string.IsNullOrWhiteSpace(clientName)) return null;
        return MachineInfo.NormalizeHostname(clientName);
    }

    /// <summary>
    /// Discovers RDP peers via the <c>CLIENTNAME</c> environment variable when
    /// port-3389 scanning finds nothing (Cloud DevBox / AVD with WebRTC transport).
    /// The CLIENTNAME is DNS-resolved to obtain a connection address.
    /// </summary>
    internal static IReadOnlyList<RdpPeer> GetClientNameFallbackPeers(string localHostname)
    {
        return GetClientNameFallbackPeers(localHostname,
            name => Dns.GetHostEntry(name));
    }

    /// <summary>
    /// Testable overload that accepts a custom DNS resolver.
    /// </summary>
    internal static IReadOnlyList<RdpPeer> GetClientNameFallbackPeers(
        string localHostname,
        Func<string, IPHostEntry> resolveHost)
    {
        var clientName = GetRdpClientName();
        if (clientName == null) return Array.Empty<RdpPeer>();
        if (clientName.Equals(localHostname, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<RdpPeer>();

        try
        {
            var entry = resolveHost(clientName);
            var addr = entry.AddressList.FirstOrDefault();
            if (addr != null)
                return new[] { new RdpPeer(clientName, addr.ToString()) };
        }
        catch { }

        // DNS failed — can't determine connection address.
        return Array.Empty<RdpPeer>();
    }
}
