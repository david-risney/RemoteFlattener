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
    public void RdpClientName_NoLongerUsedForMatching()
    {
        // RdpClientName is NOT used for window matching anymore.
        // Peer with only RdpClientName (no FriendlyName, no MachineName match) won't pair.
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

        Assert.Empty(map); // No match — RdpClientName not used
    }

    [Fact]
    public void Pass1PrioritizedOverPass2()
    {
        // Peer matches by MachineName (Pass 1), another by FriendlyName (Pass 2).
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true },
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, DevBoxFriendlyName = "my-devbox" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4",
            FakeMsrdc(("davris-0", 2), ("my-devbox", 5)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["DAVRIS-0"]);       // matched by MachineName
        Assert.Equal(5, map["CPC-DEVBOX-1"]);   // matched by FriendlyName
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
                DevBoxFriendlyName = "davris-10"   // lowercase
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
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, DevBoxFriendlyName = "workspace-a" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, DevBoxFriendlyName = "workspace-b" }
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
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, DevBoxFriendlyName = "workspace-a" },
            new() { MachineName = "CPC-DEV-B", IsRdpServer = true, DevBoxFriendlyName = "workspace-b" }
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
            new() { MachineName = "CPC-DEVBOX-1", IsRdpServer = true, DevBoxFriendlyName = "workspace-new" }
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
            new() { MachineName = "CPC-DEV-A", IsRdpServer = true, DevBoxFriendlyName = "workspace-a" }
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
            new() { MachineName = "CLIENT-A", IsRdpServer = false, DevBoxFriendlyName = "my-devbox" },
            // Server with matching FriendlyName
            new() { MachineName = "CPC-MATCH", IsRdpServer = true, DevBoxFriendlyName = "davris-10" },
            // Server with null friendly name but MachineName matches a window
            new() { MachineName = "DAVRIS-5", IsRdpServer = true },
            // Already in map
            new() { MachineName = "MSTSC-SERVER", IsRdpServer = true }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 4), ("davris-5", 6)));

        Assert.Equal(3, map.Count);
        Assert.Equal(1, map["MSTSC-SERVER"]);  // unchanged
        Assert.Equal(4, map["CPC-MATCH"]);     // matched by FriendlyName (pass 2)
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
        // Real scenario: local=DAVRIS-4, peers DAVRIS-0 and CPC-DAVRI-XXS9M.
        // DAVRIS-0 matches window "davris-0" by MachineName (Pass 1).
        // CPC-DAVRI-XXS9M matches window "davris-10" by FriendlyName (Pass 2).
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true },
            new() { MachineName = "CPC-DAVRI-XXS9M", IsRdpServer = true, DevBoxFriendlyName = "davris-10" },
            new() { MachineName = "DAVRIS-1", IsRdpServer = false }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4",
            FakeMsrdc(("davris-0", 2), ("davris-10", 5)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["DAVRIS-0"]);           // pass 1: MachineName match
        Assert.Equal(5, map["CPC-DAVRI-XXS9M"]);    // pass 2: FriendlyName match
        Assert.False(map.ContainsKey("DAVRIS-1"));  // not a server, never matched
    }

    [Fact]
    public void Pass1WindowNotReusedInPass2()
    {
        // If a window is consumed in pass 1 (MachineName), it shouldn't be matched
        // again in pass 2 (FriendlyName).
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            // This peer's MachineName "DAVRIS-10" matches window "davris-10" in pass 1.
            new() { MachineName = "DAVRIS-10", IsRdpServer = true },
            // This peer's FriendlyName "davris-10" also matches window "davris-10"
            // but it should NOT get it because pass 1 already claimed it.
            new() { MachineName = "CPC-OTHER", IsRdpServer = true, DevBoxFriendlyName = "davris-10" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC",
            FakeMsrdc(("davris-10", 5)));

        Assert.Single(map);
        Assert.Equal(5, map["DAVRIS-10"]);         // pass 1 claimed it
        Assert.False(map.ContainsKey("CPC-OTHER")); // no remaining window to match
    }

    [Fact]
    public void NoFriendlyName_NoMachineNameMatch_NotPairedWithoutDns()
    {
        // When peers have no FriendlyName and MachineName doesn't match any window,
        // nothing is paired (DNS pass needs peerAddresses to work).
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "CPC-DAVRI-XXS9M", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "DAVRIS-1", IsRdpServer = false }
        };

        // Only one msrdc window "davris-10" — no MachineName or FriendlyName match.
        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4",
            FakeMsrdc(("davris-10", 5)));

        Assert.Empty(map); // Neither is paired without DNS resolution
    }

    [Fact]
    public void AmbiguousRdpClientName_DnsResolvesToCorrectPeer()
    {
        // When no MachineName/FriendlyName match exists, Pass 3 (DNS) can pair by
        // resolving the window title to an IP and matching against peer connection IPs.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-10" },
            new() { MachineName = "CPC-DAVRI-XXS9M", IsRdpServer = true, RdpClientName = "DAVRIS-10" }
        };

        var peerAddresses = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["DAVRIS-0"] = "10.0.0.1",
            ["CPC-DAVRI-XXS9M"] = "10.0.0.2"
        };

        // DNS resolves "davris-10" to 10.0.0.2 (CPC-DAVRI-XXS9M's IP).
        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-4", peerAddresses,
            FakeMsrdc(("davris-10", 5)), title => title == "davris-10" ? "10.0.0.2" : null);

        Assert.Single(map);
        Assert.Equal(5, map["CPC-DAVRI-XXS9M"]); // DNS resolved to this peer's IP
        Assert.False(map.ContainsKey("DAVRIS-0"));
    }

    [Fact]
    public void Pass2_DevBoxFriendlyName_MatchesWindowTitle()
    {
        // DevBox reports FriendlyName="davris-10", window title is "davris-10".
        // MachineName "CPC-DEVBOX" doesn't match any window.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX",
                IsRdpServer = true,
                RdpClientName = "DAVRIS-1",
                DevBoxFriendlyName = "davris-10"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-1",
            FakeMsrdc(("davris-10", 4)));

        Assert.Single(map);
        Assert.Equal(4, map["CPC-DEVBOX"]);
    }

    [Fact]
    public void Pass2_DevBoxFriendlyName_PrioritizedOverRdpClientName()
    {
        // Two peers: CPC-DEVBOX has FriendlyName matching window, CPC-OTHER has
        // RdpClientName matching the same window. FriendlyName (Pass 2) wins.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new()
            {
                MachineName = "CPC-DEVBOX",
                IsRdpServer = true,
                DevBoxFriendlyName = "my-devbox"
            },
            new()
            {
                MachineName = "CPC-OTHER",
                IsRdpServer = true,
                RdpClientName = "MY-DEVBOX"
            }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "CLIENT-PC",
            FakeMsrdc(("my-devbox", 3)));

        Assert.Single(map);
        Assert.Equal(3, map["CPC-DEVBOX"]); // FriendlyName matched in Pass 2
        Assert.False(map.ContainsKey("CPC-OTHER")); // RdpClientName pass couldn't claim consumed window
    }

    [Fact]
    public void Pass2_DevBoxFriendlyName_AmbiguousSkipped()
    {
        // Two peers claim the same FriendlyName — Pass 2 skips both.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-A", IsRdpServer = true, DevBoxFriendlyName = "shared-name" },
            new() { MachineName = "CPC-B", IsRdpServer = true, DevBoxFriendlyName = "shared-name" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "CLIENT-PC",
            FakeMsrdc(("shared-name", 2)));

        Assert.Empty(map); // Ambiguous — neither paired
    }

    [Fact]
    public void Pass1_MachineName_StillPrioritizedOverFriendlyName()
    {
        // Peer has MachineName that matches window directly — Pass 1 wins even though
        // another peer has FriendlyName matching a different window.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true },
            new() { MachineName = "CPC-DEVBOX", IsRdpServer = true, DevBoxFriendlyName = "my-devbox" }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "CLIENT-PC",
            FakeMsrdc(("davris-0", 1), ("my-devbox", 4)));

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["DAVRIS-0"]);   // Pass 1: MachineName
        Assert.Equal(4, map["CPC-DEVBOX"]); // Pass 2: FriendlyName
    }

    [Fact]
    public void RealScenario_DevBoxWithFriendlyName()
    {
        // Real scenario: client DAVRIS-1, DevBox CPC-DAVRI-XXS9M (friendly: "davris-10"),
        // plus DAVRIS-0 which is a regular RDP server (no DevBox).
        // Window "davris-10" should match via FriendlyName (Pass 2).
        // Window "davris-0" should match via MachineName (Pass 1).
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "DAVRIS-0", IsRdpServer = true, RdpClientName = "DAVRIS-1" },
            new() { MachineName = "CPC-DAVRI-XXS9M", IsRdpServer = true, RdpClientName = "DAVRIS-1", DevBoxFriendlyName = "davris-10" },
            new() { MachineName = "DAVRIS-1", IsRdpServer = false }
        };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "DAVRIS-1",
            FakeMsrdc(("davris-0", 2), ("davris-10", 5)));

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["DAVRIS-0"]);           // Pass 1: MachineName
        Assert.Equal(5, map["CPC-DAVRI-XXS9M"]);   // Pass 2: FriendlyName
        Assert.False(map.ContainsKey("DAVRIS-1"));
    }

    [Fact]
    public void Pass4_ProcessTcpConnection_MatchesPeerIp()
    {
        // Window "unknown-title" doesn't match by name or DNS, but the owning process
        // has a TCP connection to the peer's IP address.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX", IsRdpServer = true }
        };

        var peerAddresses = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["CPC-DEVBOX"] = "10.0.0.5"
        };

        // Window PID map: "unknown-title" owned by PID 1234
        Func<Dictionary<string, uint>> fakeWindowPids = () =>
            new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["unknown-title"] = 1234
            };

        // PID 1234 has TCP connection to 10.0.0.5 (the peer's IP)
        Func<System.Collections.Generic.IEnumerable<uint>, Dictionary<uint, List<string>>> fakeTcp = pids =>
            new Dictionary<uint, List<string>>
            {
                [1234] = new List<string> { "10.0.0.5", "40.90.130.1" }
            };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", peerAddresses,
            FakeMsrdc(("unknown-title", 3)), _ => null, fakeWindowPids, fakeTcp);

        Assert.Single(map);
        Assert.Equal(3, map["CPC-DEVBOX"]);
    }

    [Fact]
    public void Pass4_NoMatchingConnection_NotPaired()
    {
        // Window's process has connections but none match peer IPs.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX", IsRdpServer = true }
        };

        var peerAddresses = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["CPC-DEVBOX"] = "10.0.0.5"
        };

        Func<Dictionary<string, uint>> fakeWindowPids = () =>
            new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["unknown-title"] = 1234
            };

        // PID 1234 connects to Azure gateway, NOT to peer
        Func<System.Collections.Generic.IEnumerable<uint>, Dictionary<uint, List<string>>> fakeTcp = pids =>
            new Dictionary<uint, List<string>>
            {
                [1234] = new List<string> { "40.90.130.1", "52.178.10.20" }
            };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", peerAddresses,
            FakeMsrdc(("unknown-title", 3)), _ => null, fakeWindowPids, fakeTcp);

        Assert.Empty(map);
    }

    [Fact]
    public void Pass4_OnlyUsedAfterEarlierPassesFail()
    {
        // Window "my-devbox" matches by FriendlyName in Pass 2 — Pass 4 should not
        // override or re-pair it.
        var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var peers = new List<MachineInfo>
        {
            new() { MachineName = "CPC-DEVBOX", IsRdpServer = true, DevBoxFriendlyName = "my-devbox" }
        };

        var peerAddresses = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["CPC-DEVBOX"] = "10.0.0.5"
        };

        Func<Dictionary<string, uint>> fakeWindowPids = () =>
            new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["my-devbox"] = 1234
            };

        Func<System.Collections.Generic.IEnumerable<uint>, Dictionary<uint, List<string>>> fakeTcp = pids =>
            new Dictionary<uint, List<string>>
            {
                [1234] = new List<string> { "10.0.0.5" }
            };

        MainWindow.MergeMsrdcDesktopEntries(map, peers, "MY-PC", peerAddresses,
            FakeMsrdc(("my-devbox", 3)), _ => null, fakeWindowPids, fakeTcp);

        Assert.Single(map);
        Assert.Equal(3, map["CPC-DEVBOX"]); // Paired by Pass 2, not Pass 4
    }
}
