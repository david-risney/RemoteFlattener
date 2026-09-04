using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteFlattener.Models;

/// <summary>
/// Pure-logic helper that computes which machines should be nested under
/// which desktops in the Desktop Map tree.  Extracted for unit testing
/// independently of WPF / live window scanning.
/// </summary>
public static class TreeNestingHelper
{
    /// <summary>
    /// Determines which machines from <paramref name="allMachines"/> should be nested
    /// under the desktops of <paramref name="client"/>, based on the client's
    /// <see cref="MachineInfo.RdpHostedServers"/> map.
    /// </summary>
    /// <param name="client">The client machine whose RdpHostedServers map is authoritative.</param>
    /// <param name="allMachines">All machines in the tree (including the local machine).</param>
    /// <param name="localMachineName">The local machine name (excluded from nesting under itself).</param>
    /// <param name="alreadyShown">Machines already placed in the tree (will not be nested again).</param>
    /// <returns>A dictionary mapping 1-based desktop index → list of machines to nest there.</returns>
    public static Dictionary<int, List<MachineInfo>> ComputeServersByDesktop(
        MachineInfo client,
        IReadOnlyList<MachineInfo> allMachines,
        string localMachineName,
        ISet<string> alreadyShown)
        => ComputeServersByDesktop(
            client,
            allMachines,
            MachineName.From(localMachineName),
            alreadyShown);

    private static Dictionary<int, List<MachineInfo>> ComputeServersByDesktop(
        MachineInfo client,
        IReadOnlyList<MachineInfo> allMachines,
        MachineName localMachine,
        ISet<string> alreadyShown)
    {
        var hostedMap = client.RdpHostedServers;
        if (hostedMap == null || hostedMap.Count == 0)
            return new Dictionary<int, List<MachineInfo>>();

        return allMachines
            .Where(m => !MachineName.From(m.MachineName).HasSameObservedValue(localMachine.Value) &&
                        !alreadyShown.Contains(m.MachineName) &&
                        hostedMap.ContainsKey(MachineName.From(m.MachineName)))
            .GroupBy(m => hostedMap[MachineName.From(m.MachineName)])
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Determines which desktop index the local server should be placed under
    /// when viewed from a remote client's perspective.
    /// Returns -1 if the local server is not found in the client's hosted map.
    /// </summary>
    public static int GetLocalServerDesktopIndex(
        MachineInfo client,
        string localMachineName)
        => GetLocalServerDesktopIndex(client, MachineName.From(localMachineName));

    private static int GetLocalServerDesktopIndex(
        MachineInfo client,
        MachineName localMachine)
    {
        var hostedMap = client.RdpHostedServers;
        if (hostedMap == null) return -1;
        return hostedMap.TryGetValue(localMachine, out var idx) ? idx : -1;
    }

    /// <summary>
    /// Computes the complete tree layout: which machines appear at the top level,
    /// and which are nested under which client's desktops.
    /// </summary>
    /// <param name="localMachineName">The local machine name.</param>
    /// <param name="localIsRdpServer">Whether the local machine is an RDP server.</param>
    /// <param name="peers">All connected peers.</param>
    /// <param name="localRdpHostedServers">The local machine's RDP hosted servers map (server→desktop index).</param>
    /// <returns>A layout describing top-level nodes and nested servers.</returns>
    public static TreeLayout ComputeLayout(
        string localMachineName,
        bool localIsRdpServer,
        IReadOnlyList<MachineInfo> peers,
        Dictionary<string, int> localRdpHostedServers)
        => ComputeLayout(
            MachineName.From(localMachineName),
            localIsRdpServer,
            peers,
            MachineDesktopMap.FromWire(localRdpHostedServers));

    private static TreeLayout ComputeLayout(
        MachineName localMachine,
        bool localIsRdpServer,
        IReadOnlyList<MachineInfo> peers,
        MachineDesktopMap localRdpHostedServers)
    {
        var localMachineInfo = new MachineInfo
        {
            MachineName = localMachine.Value,
            IsRdpServer = localIsRdpServer,
            IsConnected = true,
            RdpHostedServers = localRdpHostedServers
        };

        // Only include peers that are reachable.
        var all = new List<MachineInfo> { localMachineInfo };
        all.AddRange(peers.Where(p => p.IsConnected || p.IsIndirect ||
            (p.IsRdpServer && localRdpHostedServers.ContainsKey(
                MachineName.From(p.MachineName)))));

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        shown.Add(localMachine.Value);

        var layout = new TreeLayout();

        if (localIsRdpServer)
        {
            // Server path: find the hosting client.
            var hostingClient = peers.FirstOrDefault(p =>
                !p.IsRdpServer &&
                p.RdpHostedServers.ContainsKey(localMachine));
            hostingClient ??= peers.FirstOrDefault(p =>
                !p.IsRdpServer &&
                p.RdpPeers.Any(rp =>
                    MachineName.From(rp).Equals(localMachine)));
            hostingClient ??= peers.FirstOrDefault(p =>
                !p.IsRdpServer && (p.IsConnected || p.IsIndirect));

            if (hostingClient != null)
            {
                shown.Add(hostingClient.MachineName);
                var nestedServers = ComputeServersByDesktop(hostingClient, all, localMachine, shown);
                foreach (var kv in nestedServers)
                    foreach (var s in kv.Value)
                        shown.Add(s.MachineName);

                layout.TopLevelNodes.Add(new TreeNode(hostingClient, isLocal: false,
                    nestedServers: nestedServers,
                    localServerDesktopIndex: GetLocalServerDesktopIndex(hostingClient, localMachine)));
            }
            else
            {
                layout.TopLevelNodes.Add(new TreeNode(localMachineInfo, isLocal: true));
            }
        }
        else
        {
            // Client path: local machine at root with nested servers.
            var nestedServers = ComputeServersByDesktop(localMachineInfo, all, localMachine, shown);
            foreach (var kv in nestedServers)
                foreach (var s in kv.Value)
                    shown.Add(s.MachineName);

            layout.TopLevelNodes.Add(new TreeNode(localMachineInfo, isLocal: true,
                nestedServers: nestedServers));
        }

        // Remote client peers (not servers, not already shown).
        var clientPeers = all.Where(m => !m.IsRdpServer && !shown.Contains(m.MachineName)).ToList();
        foreach (var client in clientPeers)
        {
            shown.Add(client.MachineName);
            var nestedServers = ComputeServersByDesktop(client, all, localMachine, shown);
            foreach (var kv in nestedServers)
                foreach (var s in kv.Value)
                    shown.Add(s.MachineName);

            layout.TopLevelNodes.Add(new TreeNode(client, isLocal: false, nestedServers: nestedServers));
        }

        // Anything else.
        foreach (var machine in all.Where(m => !shown.Contains(m.MachineName)))
        {
            layout.TopLevelNodes.Add(new TreeNode(machine, isLocal: false));
        }

        return layout;
    }

    /// <summary>Represents the full tree layout.</summary>
    public class TreeLayout
    {
        public List<TreeNode> TopLevelNodes { get; } = new();
    }

    /// <summary>Represents a machine node in the tree with optional nested servers.</summary>
    public class TreeNode
    {
        public MachineInfo Machine { get; }
        public bool IsLocal { get; }
        /// <summary>Desktop index → servers nested on that desktop.</summary>
        public Dictionary<int, List<MachineInfo>> NestedServers { get; }
        /// <summary>Desktop index where the local server node should appear (-1 if N/A).</summary>
        public int LocalServerDesktopIndex { get; }

        public TreeNode(MachineInfo machine, bool isLocal,
            Dictionary<int, List<MachineInfo>>? nestedServers = null,
            int localServerDesktopIndex = -1)
        {
            Machine = machine;
            IsLocal = isLocal;
            NestedServers = nestedServers ?? new Dictionary<int, List<MachineInfo>>();
            LocalServerDesktopIndex = localServerDesktopIndex;
        }
    }
}
