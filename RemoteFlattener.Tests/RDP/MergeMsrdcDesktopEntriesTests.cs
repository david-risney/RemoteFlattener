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
    public void Pass1_WindowTitleMatchesMachineName()
    {
        // Window title "davris-0" matches peer MachineName "DAVRIS-0" directly.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-10" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-0", 3)));

        Assert.Single(map);
        Assert.Equal(3, map["DAVRIS-0"]);
    }

    [Fact]
    public void Pass2_WindowTitleMatchesRdpClientName()
    {
        // Window "davris-10" matches peer's RdpClientName="DAVRIS-10".
        // Peer MachineName is "CPC-DEVBOX-1" which doesn't match the title.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "DAVRIS-10"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 5)));

        Assert.Single(map);
        Assert.Equal(5, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void Pass1PrioritizedOverPass2()
    {
        // Two peers: DAVRIS-0 matches by MachineName (pass 1).
        // CPC-DEVBOX matches by RdpClientName (pass 2).
        // Both have same RdpClientName but pass 1 should grab the right window.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "DAVRIS-10" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4",
            FakeMsrdc(("davris-0", 2), ("davris-10", 5)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["DAVRIS-0"]);       // matched by MachineName
        Assert.Equal(5, map["CPC-DEVBOX-1"]);   // matched by RdpClientName
    }

    [Fact]
    public void NoMatchingTitle_PeerNotMerged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "WORKSPACE-X"
            }
        };

        // Window title doesn't match MachineName or RdpClientName.
        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("unrelated-window", 4)));
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
                RdpClientName = "DAVRIS-10"
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
                RdpClientName = "DAVRIS-10"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4)));
        Assert.Empty(map);
    }

    [Fact]
    public void CaseInsensitiveMatching()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX-1",
                IsRdpServer = true,
                RdpClientName = "davris-10"   // lowercase
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("DAVRIS-10", 2)));

        Assert.Single(map);
        Assert.Equal(2, map["CPC-DEVBOX-1"]);
    }

    [Fact]
    public void MultiplePeers_MatchedByTitleCorrectly()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "WORKSPACE-A" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, RdpClientName = "WORKSPACE-B" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-b", 3), ("workspace-a", 1)));

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
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "WORKSPACE-A" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, RdpClientName = "WORKSPACE-B" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-a", 5)));

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
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "DAVRIS-10" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", EmptyMsrdc());

        Assert.Empty(map);
    }

    [Fact]
    public void MsrdcWindowMatchingExistingKey_IsExcluded()
    {
        // An msrdc window whose title matches an existing mstsc entry key
        // should be excluded from pairing.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["EXISTING-SERVER"] = 2
        };
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, RdpClientName = "WORKSPACE-NEW" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("EXISTING-SERVER", 5), ("workspace-new", 3)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["EXISTING-SERVER"]); // unchanged
        Assert.Equal(3, map["CPC-DEVBOX-1"]);    // paired with remaining window
    }

    [Fact]
    public void NullRdpClientName_PeerMatchesByMachineNameOnly()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = null }
        };

        // Pass 1 can still match by MachineName.
        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-0", 4)));
        Assert.Single(map);
        Assert.Equal(4, map["DAVRIS-0"]);
    }

    [Fact]
    public void NullRdpClientName_NoMachineNameMatch_NotPaired()
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
    public void MoreWindowsThanPeers_ExtraWindowsIgnored()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, RdpClientName = "WORKSPACE-A" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("workspace-a", 1), ("workspace-b", 3), ("workspace-c", 5)));

        Assert.Single(map);
        Assert.Equal(1, map["CPC-DEV-A"]);
    }

    [Fact]
    public void MixedPeers_OnlyServersWithMatchingTitlesMerged()
    {
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["MSTSC-SERVER"] = 1
        };
        var peers = new List<MachineInfo>
        {
            // Not a server
            new() { MachineName = "CLIENT-A", IsRdpServer = false, RdpClientName = "MY-PC" },
            // Server with matching RdpClientName
            new() { MachineName = "CPC-MATCH", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            // Server with null client name but MachineName matches a window
            new() { MachineName = "DAVRIS-5", IsRdpServer = true, RdpClientName = null },
            // Already in map
            new() { MachineName = "MSTSC-SERVER", IsRdpServer = true, RdpClientName = "MY-PC" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4), ("davris-5", 6)));

        Assert.Equal(3, map.Count);
        Assert.Equal(1, map["MSTSC-SERVER"]);  // unchanged
        Assert.Equal(4, map["CPC-MATCH"]);     // matched by RdpClientName (pass 2)
        Assert.Equal(6, map["DAVRIS-5"]);      // matched by MachineName (pass 1)
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

    [Fact]
    public void RealScenario_Davris4Local_CpcAndDavris0Remote()
    {
        // Real scenario from user: local=DAVRIS-4, peers DAVRIS-0 and CPC-DAVRI-XXS9M
        // both report RdpClientName=DAVRIS-10. Windows: "davris-0" and "davris-10".
        // DAVRIS-0 should match by MachineName, CPC should match by RdpClientName.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "CPC-DAVRI-XXS9M", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "DAVRIS-1", IsRdpServer = false }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4",
            FakeMsrdc(("davris-0", 2), ("davris-10", 5)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["DAVRIS-0"]);           // pass 1: MachineName match
        Assert.Equal(5, map["CPC-DAVRI-XXS9M"]);    // pass 2: RdpClientName match
        Assert.False(map.ContainsKey("DAVRIS-1"));  // not a server, never matched
    }

    [Fact]
    public void Pass1WindowNotReusedInPass2()
    {
        // If a window is consumed in pass 1, it shouldn't be matched again in pass 2.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            // This peer's MachineName "DAVRIS-10" matches window "davris-10" in pass 1.
            new() { MachineName = "DAVRIS-10", IsRdpServer = true, RdpClientName = "SOMETHING" },
            // This peer's RdpClientName "DAVRIS-10" also matches window "davris-10"
            // but it should NOT get it because pass 1 already claimed it.
            new() { MachineName = "CPC-OTHER", IsRdpServer = true, RdpClientName = "DAVRIS-10" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 5)));

        Assert.Single(map);
        Assert.Equal(5, map["DAVRIS-10"]);         // pass 1 claimed it
        Assert.False(map.ContainsKey("CPC-OTHER")); // no remaining window to match
    }
}
