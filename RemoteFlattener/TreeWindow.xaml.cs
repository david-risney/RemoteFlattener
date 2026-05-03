using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlattener.Models;

namespace RemoteFlattener;

public partial class TreeWindow : Window
{
    public TreeWindow(
        ObservableCollection<MachineInfo> peers,
        string localMachineName,
        bool localIsRdpServer)
    {
        InitializeComponent();
        BuildTree(peers, localMachineName, localIsRdpServer);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Grab keyboard focus so Escape works immediately.
        Keyboard.Focus(this);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    private void BuildTree(
        ObservableCollection<MachineInfo> peers,
        string localMachineName,
        bool localIsRdpServer)
    {
        // Build a combined list: local machine first, then known peers.
        var localInfo = new MachineInfo
        {
            MachineName    = localMachineName,
            IsRdpServer    = localIsRdpServer,
            IsConnected    = true,
            CurrentDesktop = 0,
            TotalDesktops  = 0
        };

        var all = new List<MachineInfo> { localInfo };
        all.AddRange(peers);

        // Servers get top-level nodes; their RDP clients appear as children.
        var serversAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in all.Where(m => m.IsRdpServer))
        {
            var serverNode = MakeItem(machine, isLocal: machine.MachineName == localMachineName);
            serversAdded.Add(machine.MachineName);

            foreach (var clientName in machine.RdpPeers)
            {
                var clientInfo = all.FirstOrDefault(m =>
                    m.MachineName.Equals(clientName, StringComparison.OrdinalIgnoreCase));
                if (clientInfo != null)
                    serverNode.Items.Add(MakeItem(clientInfo, isLocal: clientInfo.MachineName == localMachineName, isChild: true));
                else
                    serverNode.Items.Add(MakeUnknownItem(clientName));
            }

            NetworkTree.Items.Add(serverNode);
        }

        // Add non-server machines that are not already shown as children of a server.
        var listedAsChild = all
            .Where(m => m.IsRdpServer)
            .SelectMany(m => m.RdpPeers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in all.Where(m => !m.IsRdpServer && !serversAdded.Contains(m.MachineName)))
        {
            if (!listedAsChild.Contains(machine.MachineName))
                NetworkTree.Items.Add(MakeItem(machine, isLocal: machine.MachineName == localMachineName));
        }
    }

    private static TreeViewItem MakeItem(MachineInfo info, bool isLocal, bool isChild = false)
    {
        var role    = info.IsRdpServer ? "[SERVER]" : "[CLIENT]";
        var desktop = info.TotalDesktops > 0
            ? $" (Desktop {info.CurrentDesktop}/{info.TotalDesktops})"
            : string.Empty;
        var localTag  = isLocal ? "  ← YOU" : string.Empty;
        var lostTag   = info.IsConnected ? string.Empty : "  [DISCONNECTED]";
        var indent    = isChild ? "  └─ " : string.Empty;

        var item = new TreeViewItem
        {
            Header     = $"{indent}{role} {info.MachineName}{desktop}{localTag}{lostTag}",
            IsExpanded = true
        };

        if (isLocal)
            item.FontWeight = FontWeights.Bold;
        if (!info.IsConnected)
            item.Foreground = Brushes.DarkGray;

        return item;
    }

    private static TreeViewItem MakeUnknownItem(string name) => new TreeViewItem
    {
        Header     = $"  └─ [CLIENT] {name}  [not connected]",
        Foreground = Brushes.DarkGray
    };

    // ── Close triggers ────────────────────────────────────────────────────────

    private void Window_MouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
