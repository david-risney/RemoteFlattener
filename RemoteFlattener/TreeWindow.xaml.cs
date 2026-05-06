using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteFlattener.Models;
using RemoteFlattener.RDP;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener;

public partial class TreeWindow : Window
{
    private readonly Action<string>                    _onTaskViewRequested;
    private readonly Action<string, int>               _onSwitchToDesktop;
    private readonly Action                            _onSettingsRequested;
    private readonly ObservableCollection<MachineInfo> _peers;
    private readonly string _localMachineName;
    private readonly bool   _localIsRdpServer;

    // Flat ordered list of every navigable row (machine header or desktop row) + its primary button.
    private readonly List<(TreeViewItem Item, Button Button)> _navItems = new();
    private int _navIndex = -1;

    // Debounce rapid back-to-back state changes into a single redraw.
    private readonly DispatcherTimer _refreshTimer;

    // Last-computed mstsc window → local desktop index mapping.
    private Dictionary<string, int> _rdpDesktopMap = new(StringComparer.OrdinalIgnoreCase);

    // Cached on each BuildTree call so DesktopRowsFor() (now an instance method) can access it.
    private VirtualDesktopProvider.DesktopInfo[] _localApiDesktops = Array.Empty<VirtualDesktopProvider.DesktopInfo>();

    /// <summary>Unified per-desktop row data used for both local and remote machines.</summary>
    private sealed record DesktopRow(
        int     Index,
        string  DisplayName,
        bool    IsCurrent,
        Guid?   Id,            // non-null only for local machine via COM API
        string? WallpaperPath, // local file path
        string? WallpaperData, // base64 JPEG received from remote machine
        string  MachineName,
        bool    IsLocal
    );

    public TreeWindow(
        ObservableCollection<MachineInfo> peers,
        string localMachineName,
        bool localIsRdpServer,
        Action<string> onTaskViewRequested,
        Action<string, int> onSwitchToDesktop,
        Action onSettingsRequested)
    {
        InitializeComponent();
        _onTaskViewRequested = onTaskViewRequested;
        _onSwitchToDesktop   = onSwitchToDesktop;
        _onSettingsRequested = onSettingsRequested;
        _peers               = peers;
        _localMachineName    = localMachineName;
        _localIsRdpServer    = localIsRdpServer;

        // Debounce: wait 200 ms after the last change before rebuilding.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshTree(); };

        // Watch collection membership changes.
        _peers.CollectionChanged += OnPeersChanged;
        // Watch property changes on each existing peer.
        foreach (var p in _peers)
            p.PropertyChanged += OnPeerPropertyChanged;

        // Watch local virtual-desktop switches.
        VirtualDesktopProvider.DesktopChanged += OnLocalDesktopChanged;

        RefreshTree();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Keyboard.Focus(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _peers.CollectionChanged -= OnPeersChanged;
        foreach (var p in _peers)
            p.PropertyChanged -= OnPeerPropertyChanged;
        VirtualDesktopProvider.DesktopChanged -= OnLocalDesktopChanged;
        base.OnClosed(e);
    }

    // ── Change subscriptions ──────────────────────────────────────────────────

    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (MachineInfo m in e.NewItems)
                m.PropertyChanged += OnPeerPropertyChanged;
        if (e.OldItems != null)
            foreach (MachineInfo m in e.OldItems)
                m.PropertyChanged -= OnPeerPropertyChanged;
        ScheduleRefresh();
    }

    private void OnPeerPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ScheduleRefresh();

    // DesktopChanged fires on the COM event thread — marshal to dispatcher.
    private void OnLocalDesktopChanged() =>
        Dispatcher.BeginInvoke(ScheduleRefresh);

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // ── Tree refresh ──────────────────────────────────────────────────────────

    private void RefreshTree()
    {
        var prevIndex = _navIndex;

        NetworkTree.Items.Clear();
        _navItems.Clear();
        _navIndex = -1;

        BuildTree();

        if (_navItems.Count > 0)
        {
            _navIndex = Math.Clamp(prevIndex < 0 ? 0 : prevIndex, 0, _navItems.Count - 1);
            ApplyNavHighlight();
        }
    }

    private void BuildTree()
    {
        var localMachineInfo = new MachineInfo
        {
            MachineName    = _localMachineName,
            IsRdpServer    = _localIsRdpServer,
            IsConnected    = true,
            CurrentDesktop = 0,
            TotalDesktops  = 0
        };

        var all = new List<MachineInfo> { localMachineInfo };
        // Only include peers that are reachable: directly connected, known via the mesh,
        // or RDP servers (which are reached through a client, not directly).
        all.AddRange(_peers.Where(p => p.IsConnected || p.IsIndirect || p.IsRdpServer));

        var localApiDesktops = VirtualDesktopProvider.GetAllDesktops();
        _localApiDesktops    = localApiDesktops;   // cache for GetDesktopRowsFor()
        var allMachineNames  = _peers.Select(p => p.MachineName).ToList();

        // Build the local RdpDesktopMap — only meaningful on a client (non-server) node.
        // This maps serverName → local desktop index via live window scan.
        // We also store it back onto localMachineInfo so BuildTree can use the same
        // code path for both local and remote client peers.
        Dictionary<string, int> localRdpMap;
        if (!_localIsRdpServer)
        {
            localRdpMap = RdpWindowLocator.GetRdpDesktopMap(
                allMachineNames.Select(MachineInfo.NormalizeHostname).ToList());
            localMachineInfo.RdpHostedServers = new Dictionary<string, int>(
                localRdpMap.ToDictionary(
                    kv => MachineInfo.NormalizeHostname(kv.Key),
                    kv => kv.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            localRdpMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        _rdpDesktopMap = localRdpMap;

        DesktopRow[] DesktopRowsFor(MachineInfo m) => GetDesktopRowsFor(m);

        var serverNodes = all
            .Where(m => m.IsRdpServer && !m.MachineName.Equals(_localMachineName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Local machine root ───────────────────────────────────────────────
        shown.Add(_localMachineName);
        var localRoot = MakeMachineItem(localMachineInfo, isLocal: true);

        if (_localIsRdpServer)
        {
            // ── Server path ─────────────────────────────────────────────────
            AddDesktopChildren(localRoot, DesktopRowsFor(localMachineInfo));

            // Find the hosting client: prefer RdpHostedServers (authoritative),
            // fall back to RdpPeers (name-only), then any connected non-server peer.
            var hostingClient = _peers.FirstOrDefault(p =>
                !p.IsRdpServer &&
                p.RdpHostedServers.ContainsKey(_localMachineName));
            hostingClient ??= _peers.FirstOrDefault(p =>
                !p.IsRdpServer &&
                p.RdpPeers.Any(rp =>
                    MachineInfo.NormalizeHostname(rp)
                        .Equals(_localMachineName, StringComparison.OrdinalIgnoreCase)));
            // Last resort: any connected/known non-server peer is likely our host.
            // This fires when the client's state hasn't been received yet.
            hostingClient ??= _peers.FirstOrDefault(p =>
                !p.IsRdpServer && (p.IsConnected || p.IsIndirect));

            if (hostingClient != null)
            {
                shown.Add(hostingClient.MachineName);
                var clientRoot = MakeMachineItem(hostingClient, isLocal: false);
                AddDesktopChildrenWithNestedServers(clientRoot, hostingClient, all, shown, localRoot);
                clientRoot.IsExpanded = true;
                NetworkTree.Items.Add(clientRoot);
            }
            else
            {
                NetworkTree.Items.Add(localRoot);
            }
        }
        else
        {
            // ── Client (local) path ──────────────────────────────────────────
            AddDesktopChildrenWithNestedServers(localRoot, localMachineInfo, all, shown, localServerNode: null);
            NetworkTree.Items.Add(localRoot);
        }

        // ── Remote client peers ──────────────────────────────────────────────
        var clientPeers = all.Where(m => !m.IsRdpServer && !shown.Contains(m.MachineName)).ToList();
        foreach (var client in clientPeers)
        {
            shown.Add(client.MachineName);
            var clientItem = MakeMachineItem(client, isLocal: false);
            AddDesktopChildren(clientItem, DesktopRowsFor(client));
            NetworkTree.Items.Add(clientItem);
        }

        // ── Anything else ────────────────────────────────────────────────────
        foreach (var machine in all.Where(m => !shown.Contains(m.MachineName)))
        {
            var item = MakeMachineItem(machine, isLocal: false);
            AddDesktopChildren(item, DesktopRowsFor(machine));
            NetworkTree.Items.Add(item);
        }
    }

    // ── Keyboard navigation ───────────────────────────────────────────────────

    private void ApplyNavHighlight()
    {
        for (int i = 0; i < _navItems.Count; i++)
        {
            _navItems[i].Item.Background = i == _navIndex
                ? new SolidColorBrush(Color.FromArgb(90, 0x60, 0xB8, 0xFF))
                : Brushes.Transparent;
        }
        if (_navIndex >= 0)
            _navItems[_navIndex].Item.BringIntoView();
    }

    // ── Item factories ────────────────────────────────────────────────────────

    // Shared rounded-corner style for all inline action buttons. Built once, frozen, reused.
    private static readonly Style _btnStyle = BuildButtonStyle();

    private static Style BuildButtonStyle()
    {
        var template = new ControlTemplate(typeof(Button));
        var border   = new FrameworkElementFactory(typeof(Border));
        border.Name  = "bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new Binding
        {
            Path           = new PropertyPath(Button.BackgroundProperty),
            RelativeSource = RelativeSource.TemplatedParent
        });
        border.SetBinding(Border.PaddingProperty, new Binding
        {
            Path           = new PropertyPath(Control.PaddingProperty),
            RelativeSource = RelativeSource.TemplatedParent
        });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty,   VerticalAlignment.Center);
        border.AppendChild(cp);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(75, 255, 255, 255)), "bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), "bd"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), "bd"));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty,                   template));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty,            Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontFamilyProperty,                 new FontFamily("Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty,                   11.0));
        style.Setters.Add(new Setter(Control.ForegroundProperty,                 Brushes.White));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,   VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty,  null));
        style.Seal();
        return style;
    }

    private TreeViewItem MakeMachineItem(MachineInfo info, bool isLocal, bool indent = false)
    {
        var dot = new Ellipse
        {
            Width             = 7,
            Height            = 7,
            Fill              = info.IsConnected
                ? new SolidColorBrush(Color.FromRgb(0x5A, 0xD0, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x55)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0)
        };

        var nameBlock = new TextBlock
        {
            Text              = info.MachineName,
            Foreground        = info.IsConnected
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x6A)),
            FontSize          = 14,
            FontWeight        = FontWeights.SemiBold,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var roleBg = info.IsRdpServer
            ? Color.FromArgb(0xFF, 0x25, 0x3D, 0x66)
            : Color.FromArgb(0xFF, 0x20, 0x45, 0x2A);
        var roleFg = info.IsRdpServer
            ? Color.FromRgb(0x7A, 0xAD, 0xFF)
            : Color.FromRgb(0x6A, 0xCC, 0x7A);
        var rolePill = new Border
        {
            Background        = new SolidColorBrush(roleBg),
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(6, 2, 6, 2),
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = info.IsRdpServer ? "SERVER" : "CLIENT",
                Foreground = new SolidColorBrush(roleFg),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI")
            }
        };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(dot);
        topRow.Children.Add(nameBlock);
        topRow.Children.Add(rolePill);
        if (isLocal)
        {
            topRow.Children.Add(new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(45, 0x60, 0xB8, 0xFF)),
                CornerRadius      = new CornerRadius(4),
                Padding           = new Thickness(6, 2, 6, 2),
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = "YOU",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xB8, 0xFF)),
                    FontSize   = 9,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI")
                }
            });
        }

        var subtitleText = !info.IsConnected
            ? "Offline"
            : info.TotalDesktops > 0
                ? $"Desktop {info.CurrentDesktop} of {info.TotalDesktops}"
                : "Connected";
        var subtitleBlock = new TextBlock
        {
            Text       = subtitleText,
            Foreground = new SolidColorBrush(info.IsConnected
                ? Color.FromRgb(0x4A, 0x4A, 0x6A)
                : Color.FromRgb(0x38, 0x38, 0x50)),
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(15, 3, 0, 0)
        };

        var leftStack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        leftStack.Children.Add(topRow);
        leftStack.Children.Add(subtitleBlock);

        var taskViewBtn = MakeTaskViewButton(info.MachineName);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(taskViewBtn, Dock.Right);
        header.Children.Add(taskViewBtn);
        header.Children.Add(leftStack);

        var item = new TreeViewItem { Header = header, IsExpanded = true };
        _navItems.Add((item, taskViewBtn));
        return item;
    }

    private void AddDesktopChildren(TreeViewItem parent, DesktopRow[] desktops)
    {
        foreach (var d in desktops)
            parent.Items.Add(MakeDesktopTreeViewItem(d));
    }

    private DesktopRow[] GetDesktopRowsFor(MachineInfo m, bool parentIsActive = true)
    {
        var isLocal = m.MachineName.Equals(_localMachineName, StringComparison.OrdinalIgnoreCase);
        if (isLocal && _localApiDesktops.Length > 0)
        {
            // Local machine: always use real API data regardless of parent context.
            return _localApiDesktops.Select(d =>
                new DesktopRow(d.Index, d.DisplayName, d.IsCurrent, d.Id, d.WallpaperPath, null, m.MachineName, true)
            ).ToArray();
        }
        if (m.TotalDesktops <= 0) return Array.Empty<DesktopRow>();
        var rows = new DesktopRow[m.TotalDesktops];
        for (int i = 0; i < m.TotalDesktops; i++)
        {
            var name  = i < m.DesktopNames.Count        ? m.DesktopNames[i]        : $"Desktop {i + 1}";
            var thumb = i < m.WallpaperThumbnails.Count ? m.WallpaperThumbnails[i] : null;
            if (string.IsNullOrEmpty(thumb)) thumb = null;
            // A remote desktop is only "current" (shows Active, Switch disabled) when the
            // parent context is also active — i.e. the full path from root is currently displayed.
            var isCurrent = parentIsActive && m.CurrentDesktop == i + 1;
            rows[i] = new DesktopRow(i + 1, name, isCurrent, null, null, thumb, m.MachineName, false);
        }
        return rows;
    }

    /// <summary>
    /// Adds desktop rows to <paramref name="clientItem"/> and, for each desktop,
    /// nests any server whose mstsc window lives there according to the client's
    /// <see cref="MachineInfo.RdpHostedServers"/> map.  Works identically whether
    /// the client is the local machine or a remote peer — both carry the same map.
    /// <paramref name="localServerNode"/> is non-null when we are the server: it is
    /// the pre-built local-machine node that should be nested on the correct desktop.
    /// </summary>
    private void AddDesktopChildrenWithNestedServers(
        TreeViewItem clientItem, MachineInfo client,
        List<MachineInfo> all, HashSet<string> shown,
        TreeViewItem? localServerNode)
    {
        var desktops = GetDesktopRowsFor(client);
        if (desktops.Length == 0) return;

        // Group remote servers by desktop index from the broadcast map.
        // hostedMap keys are always normalized short names; s.MachineName may be a FQDN,
        // so normalize before lookup.
        var hostedMap = client.RdpHostedServers;
        var serversByDesktop = all
            .Where(m => m.IsRdpServer &&
                        !m.MachineName.Equals(_localMachineName, StringComparison.OrdinalIgnoreCase))
            .Where(s => hostedMap.ContainsKey(MachineInfo.NormalizeHostname(s.MachineName)))
            .GroupBy(s => hostedMap[MachineInfo.NormalizeHostname(s.MachineName)])
            .ToDictionary(g => g.Key, g => g.ToList());

        // If we're the server, find which desktop index we belong to.
        int localServerDesktopIdx = localServerNode != null && hostedMap.ContainsKey(_localMachineName)
            ? hostedMap[_localMachineName]
            : -1;

        bool localServerPlaced = false;
        foreach (var d in desktops)
        {
            var dItem = MakeDesktopTreeViewItem(d);

            // Nest the local server node on its hosting desktop.
            if (localServerNode != null && d.Index == localServerDesktopIdx)
            {
                dItem.Items.Add(localServerNode);
                dItem.IsExpanded = true;
                localServerPlaced = true;
            }

            // Nest any remote server peers on their hosting desktop.
            if (serversByDesktop.TryGetValue(d.Index, out var serversHere))
            {
                foreach (var s in serversHere)
                {
                    shown.Add(s.MachineName);
                    var sItem = MakeMachineItem(s, isLocal: false, indent: true);
                    // Pass d.IsCurrent as parentIsActive: a server's desktop only shows
                    // "Active" and has Switch disabled when its parent client desktop is
                    // currently the active one (i.e. the mstsc window is visible).
                    AddDesktopChildren(sItem, GetDesktopRowsFor(s, parentIsActive: d.IsCurrent));
                    dItem.Items.Add(sItem);
                    dItem.IsExpanded = true;
                }
            }

            clientItem.Items.Add(dItem);
        }

        // If desktop-index data was unavailable, still nest the local server directly
        // under the client rather than leaving it orphaned or at the tree root.
        if (localServerNode != null && !localServerPlaced)
            clientItem.Items.Insert(0, localServerNode);
    }

    private TreeViewItem MakeDesktopTreeViewItem(DesktopRow d)
    {
        UIElement thumbnail;
        try
        {
            BitmapImage? bmp = null;
            if (!string.IsNullOrEmpty(d.WallpaperPath) && File.Exists(d.WallpaperPath))
            {
                using var stream = new FileStream(d.WallpaperPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bmp = LoadBitmapFromStream(stream);
            }
            else if (!string.IsNullOrEmpty(d.WallpaperData))
            {
                var bytes = Convert.FromBase64String(d.WallpaperData);
                using var ms = new MemoryStream(bytes);
                bmp = LoadBitmapFromStream(ms);
            }
            thumbnail = bmp != null
                ? new Border
                {
                    Width             = 120,
                    Height            = 68,
                    CornerRadius      = new CornerRadius(4),
                    ClipToBounds      = true,
                    Margin            = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush       = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                    BorderThickness   = new Thickness(1),
                    Child = new Image
                    {
                        Source           = bmp,
                        Stretch          = Stretch.UniformToFill,
                        StretchDirection = StretchDirection.Both
                    }
                }
                : MakeThumbnailPlaceholder();
        }
        catch { thumbnail = MakeThumbnailPlaceholder(); }

        var nameBlock = new TextBlock
        {
            Text       = d.DisplayName,
            Foreground = d.IsCurrent
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x99)),
            FontSize   = 13,
            FontWeight = d.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = new FontFamily("Segoe UI")
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(nameBlock);
        if (d.IsCurrent)
        {
            var activeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            activeRow.Children.Add(new Ellipse
            {
                Width             = 6,
                Height            = 6,
                Fill              = new SolidColorBrush(Color.FromRgb(0x5A, 0xD0, 0x6A)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 5, 0)
            });
            activeRow.Children.Add(new TextBlock
            {
                Text       = "Active",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xD0, 0x6A)),
                FontSize   = 10,
                FontFamily = new FontFamily("Segoe UI")
            });
            textStack.Children.Add(activeRow);
        }

        var desktopIndex = d.Index;
        var machineName  = d.MachineName;
        var isLocal      = d.IsLocal;
        var switchBtn    = MakeActionButton("Switch");
        switchBtn.IsEnabled  = !d.IsCurrent;
        switchBtn.MouseDown += (_, e) => e.Handled = true;
        switchBtn.Click     += (_, _) =>
        {
            // If this desktop belongs to a remote server, also switch the local machine
            // to whichever desktop hosts that server's mstsc window.
            if (!isLocal && _rdpDesktopMap.TryGetValue(MachineInfo.NormalizeHostname(machineName), out var localIdx))
                _onSwitchToDesktop(_localMachineName, localIdx);
            _onSwitchToDesktop(machineName, desktopIndex);
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(switchBtn, Dock.Right);
        row.Children.Add(switchBtn);
        row.Children.Add(thumbnail);
        row.Children.Add(textStack);

        var item = new TreeViewItem { Header = row, IsExpanded = false };
        _navItems.Add((item, switchBtn));
        return item;
    }

    private static Border MakeThumbnailPlaceholder() =>
        new Border
        {
            Width             = 120,
            Height            = 68,
            Background        = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x22)),
            CornerRadius      = new CornerRadius(4),
            BorderBrush       = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            BorderThickness   = new Thickness(1),
            Margin            = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

    private static BitmapImage LoadBitmapFromStream(Stream stream)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource     = stream;
        bmp.DecodePixelWidth = 128;
        bmp.CacheOption      = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private Button MakeTaskViewButton(string machineName)
    {
        var btn = MakeActionButton("Task View");
        btn.Click     += (_, _) => _onTaskViewRequested(machineName);
        btn.MouseDown += (_, e) => e.Handled = true;
        return btn;
    }

    private static Button MakeActionButton(string label) =>
        new Button
        {
            Content         = label,
            Style           = _btnStyle,
            Background      = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Padding         = new Thickness(12, 5, 12, 5),
            Margin          = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0)
        };
    // ── Close / keyboard ─────────────────────────────────────────────────────

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onSettingsRequested();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        while (src != null && src != this)
        {
            if (src is Border b && b.Name == "ContentPanel")
                return;
            src = VisualTreeHelper.GetParent(src);
        }
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Up:
                if (_navItems.Count > 0)
                {
                    _navIndex = _navIndex <= 0 ? _navItems.Count - 1 : _navIndex - 1;
                    ApplyNavHighlight();
                    e.Handled = true;
                }
                break;

            case Key.Down:
                if (_navItems.Count > 0)
                {
                    _navIndex = _navIndex >= _navItems.Count - 1 ? 0 : _navIndex + 1;
                    ApplyNavHighlight();
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (_navIndex >= 0 && _navIndex < _navItems.Count)
                {
                    var btn = _navItems[_navIndex].Button;
                    if (btn.IsEnabled)
                        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                }
                break;
        }
    }
}
