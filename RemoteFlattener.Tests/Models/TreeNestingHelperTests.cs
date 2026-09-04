using System.Collections.Generic;
using System.Linq;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

/// <summary>
/// Unit tests for <see cref="TreeNestingHelper"/> — verifies that RDP server
/// machines are correctly nested under the appropriate client desktop.
/// </summary>
public class TreeNestingHelperTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // ComputeServersByDesktop
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ComputeServersByDesktop_NestsServerUnderCorrectDesktop()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap
            {
                { "SERVER-A", 2 },
                { "SERVER-B", 3 }
            }
        };

        var serverA = new MachineInfo { MachineName = "SERVER-A", IsRdpServer = true, IsConnected = true };
        var serverB = new MachineInfo { MachineName = "SERVER-B", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, serverA, serverB };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Equal(2, result.Count);
        Assert.Single(result[2]);
        Assert.Equal("SERVER-A", result[2][0].MachineName);
        Assert.Single(result[3]);
        Assert.Equal("SERVER-B", result[3][0].MachineName);
    }

    [Fact]
    public void ComputeServersByDesktop_EmptyHostedMap_ReturnsEmpty()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap()
        };

        var server = new MachineInfo { MachineName = "SERVER", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, server };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeServersByDesktop_IgnoresLocalMachine()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap { { "CLIENT", 1 } }
        };

        var all = new List<MachineInfo> { client };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeServersByDesktop_IgnoresAlreadyShownMachines()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap { { "SERVER-A", 2 } }
        };

        var serverA = new MachineInfo { MachineName = "SERVER-A", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, serverA };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT", "SERVER-A" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeServersByDesktop_MatchesCaseInsensitively()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap
            {
                { "DAVRIS-0", 2 }
            }
        };

        // Machine name uses different case than hostedMap key.
        var server = new MachineInfo { MachineName = "davris-0", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, server };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Single(result);
        Assert.Equal("davris-0", result[2][0].MachineName);
    }

    [Fact]
    public void ComputeServersByDesktop_NestsNonRdpServerIfInHostedMap()
    {
        // A peer may not self-report IsRdpServer=true (running in console session)
        // but still be in the hosted map because we see an mstsc window for it.
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap { { "PEER", 3 } }
        };

        var peer = new MachineInfo { MachineName = "PEER", IsRdpServer = false, IsConnected = true };
        var all = new List<MachineInfo> { client, peer };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Single(result);
        Assert.Equal("PEER", result[3][0].MachineName);
    }

    [Fact]
    public void ComputeServersByDesktop_MultipleServersOnSameDesktop()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap
            {
                { "SERVER-A", 2 },
                { "SERVER-B", 2 }
            }
        };

        var serverA = new MachineInfo { MachineName = "SERVER-A", IsRdpServer = true, IsConnected = true };
        var serverB = new MachineInfo { MachineName = "SERVER-B", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, serverA, serverB };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Single(result); // only desktop 2
        Assert.Equal(2, result[2].Count);
    }

    [Fact]
    public void ComputeServersByDesktop_HandlesNormalizedFQDN()
    {
        // hostedMap stores normalized short name, peer uses FQDN.
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap
            {
                { "DAVRIS-0", 2 }
            }
        };

        var server = new MachineInfo { MachineName = "davris-0.corp.microsoft.com", IsRdpServer = true, IsConnected = true };
        var all = new List<MachineInfo> { client, server };
        var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "CLIENT" };

        var result = TreeNestingHelper.ComputeServersByDesktop(client, all, "CLIENT", shown);

        Assert.Single(result);
        Assert.Equal("davris-0.corp.microsoft.com", result[2][0].MachineName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetLocalServerDesktopIndex
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetLocalServerDesktopIndex_ReturnsIndex_WhenInMap()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            RdpHostedServers = new MachineDesktopMap
            {
                { "SERVER", 4 }
            }
        };

        Assert.Equal(4, TreeNestingHelper.GetLocalServerDesktopIndex(client, "SERVER"));
    }

    [Fact]
    public void GetLocalServerDesktopIndex_ReturnsNegativeOne_WhenNotInMap()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            RdpHostedServers = new MachineDesktopMap()
        };

        Assert.Equal(-1, TreeNestingHelper.GetLocalServerDesktopIndex(client, "SERVER"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ComputeLayout — full tree structure tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ComputeLayout_ClientPath_NestsServersUnderLocalDesktops()
    {
        var serverA = new MachineInfo { MachineName = "SERVER-A", IsRdpServer = true, IsConnected = true, TotalDesktops = 2 };
        var serverB = new MachineInfo { MachineName = "SERVER-B", IsRdpServer = true, IsConnected = true, TotalDesktops = 3 };
        var peers = new List<MachineInfo> { serverA, serverB };
        var hostedMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "SERVER-A", 2 },
            { "SERVER-B", 4 }
        };

        var layout = TreeNestingHelper.ComputeLayout("DAVRIS-4", localIsRdpServer: false, peers, hostedMap);

        // Only one top-level node: the local machine.
        Assert.Single(layout.TopLevelNodes);
        var localNode = layout.TopLevelNodes[0];
        Assert.True(localNode.IsLocal);
        Assert.Equal("DAVRIS-4", localNode.Machine.MachineName);

        // Both servers are nested.
        Assert.Equal(2, localNode.NestedServers.Count);
        Assert.Single(localNode.NestedServers[2]);
        Assert.Equal("SERVER-A", localNode.NestedServers[2][0].MachineName);
        Assert.Single(localNode.NestedServers[4]);
        Assert.Equal("SERVER-B", localNode.NestedServers[4][0].MachineName);
    }

    [Fact]
    public void ComputeLayout_ClientPath_ServerNotInHostedMap_ShowsAtTopLevel()
    {
        var serverA = new MachineInfo { MachineName = "SERVER-A", IsRdpServer = true, IsConnected = true };
        var peers = new List<MachineInfo> { serverA };
        // Empty hosted map — no mstsc windows found.
        var hostedMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        var layout = TreeNestingHelper.ComputeLayout("CLIENT", localIsRdpServer: false, peers, hostedMap);

        // Local node + SERVER-A as separate top-level (in "Anything else" bucket).
        Assert.Equal(2, layout.TopLevelNodes.Count);
        Assert.Equal("CLIENT", layout.TopLevelNodes[0].Machine.MachineName);
        Assert.Equal("SERVER-A", layout.TopLevelNodes[1].Machine.MachineName);
        Assert.Empty(layout.TopLevelNodes[0].NestedServers);
    }

    [Fact]
    public void ComputeLayout_ServerPath_LocalServerNestedUnderClient()
    {
        var client = new MachineInfo
        {
            MachineName = "CLIENT",
            IsRdpServer = false,
            IsConnected = true,
            RdpHostedServers = new MachineDesktopMap
            {
                { "SERVER", 3 }
            }
        };
        var peers = new List<MachineInfo> { client };
        var hostedMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        var layout = TreeNestingHelper.ComputeLayout("SERVER", localIsRdpServer: true, peers, hostedMap);

        // The hosting client is the top-level node (contains our server).
        Assert.Single(layout.TopLevelNodes);
        var clientNode = layout.TopLevelNodes[0];
        Assert.Equal("CLIENT", clientNode.Machine.MachineName);
        Assert.Equal(3, clientNode.LocalServerDesktopIndex);
    }

    [Fact]
    public void ComputeLayout_ClientPath_MixedServersAndClientPeers()
    {
        var server = new MachineInfo { MachineName = "SERVER", IsRdpServer = true, IsConnected = true };
        var otherClient = new MachineInfo { MachineName = "OTHER-CLIENT", IsRdpServer = false, IsConnected = true };
        var peers = new List<MachineInfo> { server, otherClient };
        var hostedMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "SERVER", 2 }
        };

        var layout = TreeNestingHelper.ComputeLayout("LOCAL", localIsRdpServer: false, peers, hostedMap);

        // Local node (with SERVER nested) + OTHER-CLIENT at top level.
        Assert.Equal(2, layout.TopLevelNodes.Count);
        Assert.Equal("LOCAL", layout.TopLevelNodes[0].Machine.MachineName);
        Assert.Single(layout.TopLevelNodes[0].NestedServers[2]);
        Assert.Equal("SERVER", layout.TopLevelNodes[0].NestedServers[2][0].MachineName);
        Assert.Equal("OTHER-CLIENT", layout.TopLevelNodes[1].Machine.MachineName);
    }

    [Fact]
    public void ComputeLayout_ClientPath_DisconnectedServerInHostedMap_StillNested()
    {
        // A peer that's IsRdpServer + in hostedMap but not IsConnected/IsIndirect
        // should still appear (it's included in `all` due to the hostedMap check).
        var server = new MachineInfo { MachineName = "SERVER", IsRdpServer = true, IsConnected = false };
        var peers = new List<MachineInfo> { server };
        var hostedMap = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "SERVER", 1 }
        };

        var layout = TreeNestingHelper.ComputeLayout("CLIENT", localIsRdpServer: false, peers, hostedMap);

        Assert.Single(layout.TopLevelNodes);
        Assert.Single(layout.TopLevelNodes[0].NestedServers[1]);
        Assert.Equal("SERVER", layout.TopLevelNodes[0].NestedServers[1][0].MachineName);
    }
}
