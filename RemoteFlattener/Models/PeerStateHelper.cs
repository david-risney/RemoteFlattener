using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteFlattener.Models;

/// <summary>
/// Pure-logic helpers for managing peer state.  Extracted from MainWindow so
/// the disconnect / indirect-recalculation rules can be unit-tested without WPF.
/// </summary>
internal static class PeerStateHelper
{
    /// <summary>
    /// Clears transient state from a peer that has just disconnected.
    /// The peer entry is kept (so the user can see it was configured) but all
    /// relationship data that may be stale is wiped.
    /// </summary>
    public static void ClearDisconnectedPeerState(MachineInfo peer)
    {
        peer.IsConnected = false;
        peer.RdpPeers = new();
        peer.RdpHostedServers = new();
        peer.RdpClientName = null;
    }

    /// <summary>
    /// Recomputes <see cref="MachineInfo.IsIndirect"/> for every peer in
    /// <paramref name="allPeers"/> based solely on the RdpPeers lists of
    /// currently-connected peers.  Peers that were only reachable through a
    /// now-disconnected node become non-indirect (and thus invisible in the tree).
    /// </summary>
    public static void RecalculateIndirectPeers(
        IEnumerable<MachineInfo> allPeers, string localMachineName)
    {
        var peers = allPeers as IList<MachineInfo> ?? allPeers.ToList();

        var indirectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in peers.Where(p => p.IsConnected))
            foreach (var rp in peer.RdpPeers)
                if (!string.IsNullOrWhiteSpace(rp))
                    indirectNames.Add(MachineName.From(rp).Canonical);

        // We are never "indirect" to ourselves.
        indirectNames.Remove(MachineName.From(localMachineName).Canonical);

        foreach (var peer in peers)
        {
            // Never mark a directly-connected peer as indirect.
            peer.IsIndirect = !peer.IsConnected && indirectNames.Contains(
                MachineName.From(peer.MachineName).Canonical);
        }
    }
}
