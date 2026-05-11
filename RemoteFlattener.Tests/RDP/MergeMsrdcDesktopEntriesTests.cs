using System.Collections.Generic;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.RDP;

public class MergeMsrdcDesktopEntriesTests
{
    /// <summary>Returns a mock msrdc map factory with the given entries.</summary>
    private static System.Func<Dictionary<string, int>> FakeMsrdc(
        params (string title, int desktop)[] entries)
    {
        return () =>
        {
            var d = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (title, desktop) in entries) d[title] = desktop;
            return d;
        };
    }

    private static System.Func<Dictionary<string, int>> EmptyMsrdc() =>
        () => new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NoDevBoxPeers_MapUnchanged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["DAVRIS-0"] = 2
        };
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", EmptyMsrdc());

        Assert.Single(map);
        Assert.Equal(2, map["DAVRIS-0"]);
    }

    [Fact]
    public void DevBoxPeer_MatchesLocalName_MergesEntry()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "MY-PC"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));

        Assert.Single(map);
        Assert.Equal(4, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void DevBoxPeer_ClientNameDoesNotMatch_NotMerged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "OTHER-PC"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));
        Assert.Empty(map);
    }

    [Fact]
    public void DevBoxPeer_AlreadyInMap_NotDuplicated()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["CPC-DEVBOX-1"] = 3
        };
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "MY-PC"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));

        // Already present — should not be overwritten or duplicated.
        Assert.Single(map);
        Assert.Equal(3, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void ClientPeer_NotRdpServer_Ignored()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "PEER-A",
                IsRdpServer = false,
                RdpClientName = "MY-PC"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));
        Assert.Empty(map);
    }

    [Fact]
    public void ClientNameComparison_IsCaseInsensitive()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "my-pc"   // lowercase
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 2)));

        Assert.Single(map);
        Assert.Equal(2, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void MultipleDevBoxPeers_PairedByOrder()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "MY-PC" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-1", 1), ("workspace-2", 3)));

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["CPC-DEV-A"]);
        Assert.Equal(3, map["CPC-DEV-B"]);
    }

    [Fact]
    public void MorePeersThanWindows_ExtraPeersUnmapped()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "MY-PC" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-1", 5)));

        Assert.Single(map);
        Assert.Equal(5, map["CPC-DEV-A"]);
        Assert.False(map.ContainsKey("CPC-DEV-B"));
    }

    [Fact]
    public void NoMsrdcWindows_MapUnchanged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", EmptyMsrdc());

        Assert.Empty(map);
    }

    [Fact]
    public void MsrdcWindowMatchingExistingKey_IsExcluded()
    {
        // An msrdc window whose title happens to match an existing mstsc entry
        // should not cause a conflict.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["EXISTING-SERVER"] = 2
        };
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("EXISTING-SERVER", 5), ("workspace-new", 3)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["EXISTING-SERVER"]); // unchanged
        Assert.Equal(3, map["CPC-DEVBOX-1"]);    // paired with remaining window
    }

    [Fact]
    public void NullRdpClientName_PeerIsIgnored()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = null }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-1", 4)));
        Assert.Empty(map);
    }

    [Fact]
    public void EmptyRdpClientName_PeerIsIgnored()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-1", 4)));
        Assert.Empty(map);
    }

    [Fact]
    public void MoreWindowsThanPeers_ExtraWindowsIgnored()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-1", 1), ("workspace-2", 3), ("workspace-3", 5)));

        Assert.Single(map);
        Assert.Equal(1, map["CPC-DEV-A"]);
    }

    [Fact]
    public void FqdnLocalMachineName_StillMatchesPeer()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        // localMachineName as FQDN — should normalize to "MY-PC" and match.
        MainWindow.MergeMsrdcDesktopEntries(map, peers, "my-pc.corp.example.com",
            FakeMsrdc(("workspace-1", 2)));

        Assert.Single(map);
        Assert.Equal(2, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void MixedPeers_OnlyMatchingDevBoxesMerged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["MSTSC-SERVER"] = 1
        };
        var peers = new List<MachineInfo>
        {
            // Not a server
            new() { MachineName = "CLIENT-A", IsRdpServer = false, RdpClientName = "MY-PC" },
            // Server but different client
            new() { MachineName = "CPC-OTHER", IsRdpServer = true, RdpClientName = "OTHER-PC" },
            // Server with null client name
            new() { MachineName = "CPC-NULL", IsRdpServer = true, RdpClientName = null },
            // Already in map
            new() { MachineName = "MSTSC-SERVER", IsRdpServer = true, RdpClientName = "MY-PC" },
            // This one should be merged
            new() { MachineName = "CPC-MATCH", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["MSTSC-SERVER"]);  // unchanged
        Assert.Equal(4, map["CPC-MATCH"]);     // the only one that should be merged
    }

    [Fact]
    public void EmptyPeersList_MapUnchanged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["EXISTING"] = 2
        };

        MainWindow.MergeMsrdcDesktopEntries(map, new List<MachineInfo>(), "MY-PC",
            FakeMsrdc(("workspace", 4)));

        Assert.Single(map);
        Assert.Equal(2, map["EXISTING"]);
    }
}
