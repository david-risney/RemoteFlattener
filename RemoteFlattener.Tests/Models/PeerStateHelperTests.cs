using System.Collections.Generic;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

/// <summary>
/// Unit tests for <see cref="PeerStateHelper"/> — the pure-logic peer state
/// management extracted from MainWindow.
/// </summary>
public class PeerStateHelperTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // ClearDisconnectedPeerState
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ClearDisconnectedPeerState_SetsIsConnectedFalse()
    {
        var peer = new MachineInfo { MachineName = "LAPTOP", IsConnected = true };
        PeerStateHelper.ClearDisconnectedPeerState(peer);
        Assert.False(peer.IsConnected);
    }

    [Fact]
    public void ClearDisconnectedPeerState_ClearsRdpPeers()
    {
        var peer = new MachineInfo
        {
            MachineName = "LAPTOP",
            IsConnected = true,
            RdpPeers = new List<string> { "SERVER-1", "SERVER-2" }
        };
        PeerStateHelper.ClearDisconnectedPeerState(peer);
        Assert.Empty(peer.RdpPeers);
    }

    [Fact]
    public void ClearDisconnectedPeerState_ClearsRdpHostedServers()
    {
        var peer = new MachineInfo
        {
            MachineName = "LAPTOP",
            IsConnected = true,
            RdpHostedServers = new Dictionary<string, int> { { "SERVER-1", 0 } }
        };
        PeerStateHelper.ClearDisconnectedPeerState(peer);
        Assert.Empty(peer.RdpHostedServers);
    }

    [Fact]
    public void ClearDisconnectedPeerState_ClearsRdpClientName()
    {
        var peer = new MachineInfo
        {
            MachineName = "SERVER-1",
            IsConnected = true,
            RdpClientName = "LAPTOP"
        };
        PeerStateHelper.ClearDisconnectedPeerState(peer);
        Assert.Null(peer.RdpClientName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RecalculateIndirectPeers — basic scenarios
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RecalculateIndirect_DisconnectedPeer_LosesIndirectFlag()
    {
        // Scenario: LAPTOP listed SERVER in its RdpPeers, making SERVER indirect.
        // LAPTOP disconnects → SERVER should no longer be indirect.
        var laptop = new MachineInfo
        {
            MachineName = "LAPTOP",
            IsConnected = false,
            RdpPeers = new List<string> { "SERVER" }
        };
        var server = new MachineInfo
        {
            MachineName = "SERVER",
            IsConnected = false,
            IsIndirect = true  // was set when LAPTOP was connected
        };
        var peers = new List<MachineInfo> { laptop, server };

        PeerStateHelper.RecalculateIndirectPeers(peers, "LOCAL");

        Assert.False(server.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_StillConnectedPeer_KeepsIndirectForItsRdpPeers()
    {
        // LAPTOP is still connected and lists SERVER in RdpPeers.
        // SERVER is not directly connected but should remain indirect.
        var laptop = new MachineInfo
        {
            MachineName = "LAPTOP",
            IsConnected = true,
            RdpPeers = new List<string> { "SERVER" }
        };
        var server = new MachineInfo
        {
            MachineName = "SERVER",
            IsConnected = false,
            IsIndirect = true
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { laptop, server }, "LOCAL");

        Assert.True(server.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_DirectlyConnectedPeer_NeverMarkedIndirect()
    {
        // Even if another peer lists it in RdpPeers, a directly-connected
        // peer should have IsIndirect = false.
        var laptop = new MachineInfo
        {
            MachineName = "LAPTOP",
            IsConnected = true,
            RdpPeers = new List<string> { "SERVER" }
        };
        var server = new MachineInfo
        {
            MachineName = "SERVER",
            IsConnected = true,
            IsIndirect = true  // incorrectly set
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { laptop, server }, "LOCAL");

        Assert.False(server.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_LocalMachine_NeverMarkedIndirect()
    {
        // A connected peer lists "LOCAL" in its RdpPeers — we must not
        // mark ourselves as indirect.
        var peer = new MachineInfo
        {
            MachineName = "PEER-A",
            IsConnected = true,
            RdpPeers = new List<string> { "LOCAL" }
        };
        var local = new MachineInfo
        {
            MachineName = "LOCAL",
            IsConnected = false,
            IsIndirect = false
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { peer, local }, "LOCAL");

        Assert.False(local.IsIndirect);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RecalculateIndirectPeers — the original bug scenario
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RecalculateIndirect_OriginalBug_LaptopGoesOffline_ServerBecomesRoot()
    {
        // Original bug: davris-10 (LAPTOP) was RDP client hosting davris-4 (SERVER).
        // LAPTOP disconnects and goes offline.  SERVER was marked IsIndirect
        // because LAPTOP's RdpPeers used to list it.  After recalculation,
        // SERVER should not be indirect (it should disappear from the tree
        // if it's neither connected nor indirect).
        //
        // davris-4 (local machine) is not in the peers list — it's "us".
        var laptop = new MachineInfo
        {
            MachineName = "DAVRIS-10",
            IsConnected = false,  // went offline
            IsIndirect = true,    // was previously known via mesh
            RdpPeers = new List<string>(),  // cleared by ClearDisconnectedPeerState
            RdpHostedServers = new Dictionary<string, int>(),
            RdpClientName = null
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { laptop }, "DAVRIS-4");

        // The laptop should no longer be indirect — it's gone.
        Assert.False(laptop.IsIndirect);
        // And it's not connected either — so it won't appear in the tree.
        Assert.False(laptop.IsConnected);
    }

    [Fact]
    public void FullDisconnectSequence_OriginalBug_LaptopClearedAndRecalculated()
    {
        // End-to-end simulation of the original bug fix:
        // 1. LAPTOP was connected with RdpPeers = ["DAVRIS-4"]
        // 2. SERVER-X was marked IsIndirect because LAPTOP listed it
        // 3. LAPTOP disconnects
        // 4. ClearDisconnectedPeerState wipes LAPTOP's state
        // 5. RecalculateIndirectPeers removes stale IsIndirect from SERVER-X
        var laptop = new MachineInfo
        {
            MachineName = "DAVRIS-10",
            IsConnected = true,
            IsIndirect = false,
            RdpPeers = new List<string> { "DAVRIS-4", "SERVER-X" },
            RdpHostedServers = new Dictionary<string, int>
            {
                { "DAVRIS-4", 0 }, { "SERVER-X", 1 }
            },
            RdpClientName = null
        };
        var serverX = new MachineInfo
        {
            MachineName = "SERVER-X",
            IsConnected = false,
            IsIndirect = true  // was set when LAPTOP was connected
        };
        var peers = new List<MachineInfo> { laptop, serverX };

        // Step 1: simulate disconnect
        PeerStateHelper.ClearDisconnectedPeerState(laptop);

        // Step 2: recalculate
        PeerStateHelper.RecalculateIndirectPeers(peers, "DAVRIS-4");

        // LAPTOP should be completely inert.
        Assert.False(laptop.IsConnected);
        Assert.False(laptop.IsIndirect);
        Assert.Empty(laptop.RdpPeers);
        Assert.Empty(laptop.RdpHostedServers);
        Assert.Null(laptop.RdpClientName);

        // SERVER-X should no longer be indirect.
        Assert.False(serverX.IsIndirect);
        Assert.False(serverX.IsConnected);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // RecalculateIndirectPeers — multi-peer / mesh scenarios
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RecalculateIndirect_TwoConnectedPeers_BothListSameIndirect()
    {
        // Two connected peers both list "REMOTE" in RdpPeers.
        // If one disconnects, REMOTE should stay indirect via the other.
        var peerA = new MachineInfo
        {
            MachineName = "PEER-A",
            IsConnected = true,
            RdpPeers = new List<string> { "REMOTE" }
        };
        var peerB = new MachineInfo
        {
            MachineName = "PEER-B",
            IsConnected = false,  // disconnected
            RdpPeers = new List<string> { "REMOTE" }
        };
        var remote = new MachineInfo
        {
            MachineName = "REMOTE",
            IsConnected = false,
            IsIndirect = true
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { peerA, peerB, remote }, "LOCAL");

        // REMOTE stays indirect because PEER-A (still connected) lists it.
        Assert.True(remote.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_AllPeersDisconnected_NoIndirectPeers()
    {
        var peerA = new MachineInfo
        {
            MachineName = "PEER-A",
            IsConnected = false,
            RdpPeers = new List<string> { "PEER-B" }
        };
        var peerB = new MachineInfo
        {
            MachineName = "PEER-B",
            IsConnected = false,
            IsIndirect = true,
            RdpPeers = new List<string> { "PEER-A" }
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { peerA, peerB }, "LOCAL");

        Assert.False(peerA.IsIndirect);
        Assert.False(peerB.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_EmptyPeerList_NoException()
    {
        PeerStateHelper.RecalculateIndirectPeers(new List<MachineInfo>(), "LOCAL");
        // No assertion needed — just verifying no exception.
    }

    [Fact]
    public void RecalculateIndirect_WhitespaceRdpPeers_AreIgnored()
    {
        var peer = new MachineInfo
        {
            MachineName = "PEER-A",
            IsConnected = true,
            RdpPeers = new List<string> { "", " ", null! }
        };
        var other = new MachineInfo
        {
            MachineName = "OTHER",
            IsConnected = false,
            IsIndirect = true
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { peer, other }, "LOCAL");

        // OTHER should not be indirect — only whitespace entries in RdpPeers.
        Assert.False(other.IsIndirect);
    }

    [Fact]
    public void RecalculateIndirect_CaseInsensitive_MatchesByNormalizedName()
    {
        // RdpPeers entry is FQDN, peer MachineName is short — should still match.
        var connected = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpPeers = new List<string> { "server.corp.com" }
        };
        var server = new MachineInfo
        {
            MachineName = "SERVER",
            IsConnected = false,
            IsIndirect = false
        };

        PeerStateHelper.RecalculateIndirectPeers(new[] { connected, server }, "LOCAL");

        Assert.True(server.IsIndirect);
    }
}
