using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace RemoteFlattener.RDP;

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
    /// Returns the hostnames (or IP strings if reverse-DNS fails) of all machines
    /// currently connected via RDP, excluding loopback and the local machine itself.
    /// </summary>
    public static IReadOnlyList<string> GetRdpPeerHostnames()
    {
        var localHostname = Environment.MachineName;
        var remoteAddresses = new HashSet<IPAddress>();

        foreach (var conn in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            if (conn.State != TcpState.Established) continue;

            // Collect the remote address when either end is port 3389.
            if (conn.LocalEndPoint.Port == RdpPort)
                remoteAddresses.Add(conn.RemoteEndPoint.Address);
            else if (conn.RemoteEndPoint.Port == RdpPort)
                remoteAddresses.Add(conn.RemoteEndPoint.Address);
        }

        var results = new List<string>();
        foreach (var addr in remoteAddresses)
        {
            if (IPAddress.IsLoopback(addr)) continue;

            string hostname;
            try
            {
                hostname = Dns.GetHostEntry(addr).HostName;
                // Strip the FQDN to just the short machine name so it matches what
                // Environment.MachineName returns on the peer.
                var dot = hostname.IndexOf('.');
                if (dot > 0) hostname = hostname[..dot];
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
