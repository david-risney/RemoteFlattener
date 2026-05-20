using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteFlattener.Logging;
using RemoteFlattener.Models;
using RemoteFlattener.RDP;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener;

public partial class TreeWindow : Window
{
    // ── Win32 interop for multi-monitor positioning ───────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly Action<string>                    _onTaskViewRequested;
    private readonly Action<string, int>               _onSwitchToDesktop;
    private readonly Action                            _onSettingsRequested;
    private readonly ObservableCollection<MachineInfo> _peers;
    private readonly string _localMachineName;
    private readonly bool   _localIsRdpServer;

    // Flat ordered list of every navigable row (machine header or desktop row) + its action.
    private readonly List<(TreeViewItem Item, Action Action)> _navItems = new();
    private int _navIndex = -1;
    private int _currentDesktopNavIndex = -1; // index of the local current desktop in _navItems

    // Debounce rapid back-to-back state changes into a single redraw.
    private readonly DispatcherTimer _refreshTimer;

    // Polls for changes that lack events (mstsc window positions) and serves
    // as a fallback when VirtualDesktop COM events are unavailable.
    private readonly DispatcherTimer _pollTimer;

    // Last-computed mstsc window → local desktop index mapping.
    private Dictionary<string, int> _rdpDesktopMap = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot of desktop count and names from the last build — used for dirty-checking.
    private int _cachedDesktopCount;
    private string _cachedDesktopSignature = string.Empty;

    // Cached on each BuildTree call so DesktopRowsFor() (now an instance method) can access it.
    private VirtualDesktopProvider.DesktopInfo[] _localApiDesktops = Array.Empty<VirtualDesktopProvider.DesktopInfo>();

    // Tracks which monitor the overlay is currently displayed on so we can
    // detect when the cursor moves to a different monitor and reposition.
    private IntPtr _currentMonitor;

    /// <summary>Unified per-desktop row data used for both local and remote machines.</summary>
    private sealed record DesktopRow(
        int     Index,
        string  DisplayName,
        bool    IsCurrent,
        Guid?   Id,            // non-null only for local machine via COM API
        string? WallpaperPath, // local file path
        string? WallpaperData, // base64 JPEG received from remote machine
        string? BackgroundColor, // hex RGB (e.g. "#1E1E2E") when desktop uses solid colour
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

        // Poll for changes that lack events (mstsc window positions, desktop
        // add/remove/rename when COM events are unavailable on this OS build).
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, _) => PollForChanges();

        // Watch collection membership changes.
        _peers.CollectionChanged += OnPeersChanged;
        // Watch property changes on each existing peer.
        foreach (var p in _peers)
            p.PropertyChanged += OnPeerPropertyChanged;

        // Watch local virtual-desktop switches.
        VirtualDesktopProvider.DesktopChanged += OnLocalDesktopChanged;

        RefreshTree();
        _pollTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PositionOnActiveMonitor();
        Keyboard.Focus(this);
    }

    /// <summary>
    /// Positions and sizes this window to cover the monitor that the user is
    /// actively using.  Prefers the monitor containing the foreground window;
    /// falls back to the monitor under the mouse cursor (handles empty desktops
    /// where no window has focus).
    /// </summary>
    private void PositionOnActiveMonitor()
    {
        var hMonitor = GetActiveMonitor();
        if (hMonitor == IntPtr.Zero) return;

        _currentMonitor = hMonitor;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
            return;

        var source = PresentationSource.FromVisual(this);
        double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        double monLeft   = mi.rcMonitor.Left   * dpiScaleX;
        double monTop    = mi.rcMonitor.Top    * dpiScaleY;
        double monWidth  = (mi.rcMonitor.Right  - mi.rcMonitor.Left) * dpiScaleX;
        double monHeight = (mi.rcMonitor.Bottom - mi.rcMonitor.Top)  * dpiScaleY;

        // Center the window on the active monitor.
        Left = monLeft + (monWidth  - ActualWidth)  / 2;
        Top  = monTop  + (monHeight - ActualHeight) / 2;
    }

    /// <summary>
    /// Returns the monitor handle for the user's current context: foreground
    /// window first, then cursor position as fallback.
    /// </summary>
    private IntPtr GetActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        var hMonitor = foreground != IntPtr.Zero
            ? MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;

        if (hMonitor == IntPtr.Zero || foreground == IntPtr.Zero)
        {
            if (GetCursorPos(out var pt))
                hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }

        return hMonitor;
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _pollTimer.Stop();
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

    /// <summary>
    /// Periodically checks for changes that lack events: mstsc window positions
    /// and (as a fallback) desktop count/name changes if VirtualDesktop COM events
    /// are unavailable on this OS build.
    /// </summary>
    private void PollForChanges()
    {
        if (_navKeyActive) return;

        bool dirty = false;

        // Check mstsc/msrdc window → virtual desktop mapping (no event exists for this).
        if (!_localIsRdpServer)
        {
            var allNames = _peers.Select(p => MachineInfo.NormalizeHostname(p.MachineName)).ToList();
            var currentMap = RdpWindowLocator.GetRdpDesktopMap(allNames);
            MainWindow.MergeMsrdcDesktopEntries(currentMap, _peers, _localMachineName);
            if (!RdpDesktopMapEquals(_rdpDesktopMap, currentMap))
                dirty = true;
        }

        // Check desktop count and names — serves as a fallback when COM events
        // (Created/Destroyed/Renamed) aren't firing on this OS build.
        var desktops = VirtualDesktopProvider.GetAllDesktops();
        var sig = BuildDesktopSignature(desktops);
        if (desktops.Length != _cachedDesktopCount || sig != _cachedDesktopSignature)
            dirty = true;

        if (dirty)
            ScheduleRefresh();

        // Move the overlay to follow the cursor if the user moved to a different monitor.
        if (IsLoaded && _currentMonitor != IntPtr.Zero)
        {
            var newMonitor = GetActiveMonitor();
            if (newMonitor != IntPtr.Zero && newMonitor != _currentMonitor)
                PositionOnActiveMonitor();
        }
    }

    private static bool RdpDesktopMapEquals(
        Dictionary<string, int> a,
        Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        return true;
    }

    private static string BuildDesktopSignature(VirtualDesktopProvider.DesktopInfo[] desktops)
    {
        if (desktops.Length == 0) return string.Empty;
        // Cheap signature: count + concatenated names + current index.
        var sb = new System.Text.StringBuilder();
        foreach (var d in desktops)
        {
            sb.Append(d.Index).Append(':').Append(d.DisplayName)
              .Append(':').Append(d.IsCurrent ? '1' : '0').Append('|');
        }
        return sb.ToString();
    }

    // Suppresses tree rebuilds while the user is actively navigating with arrow keys,
    // so the highlight doesn't jump or reset mid-keypress.
    private bool _navKeyActive;

    private void ScheduleRefresh()
    {
        if (_navKeyActive) return;
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
        _currentDesktopNavIndex = -1;

        BuildTree();
        CollectNavItems();

        if (_navItems.Count > 0)
        {
            // On first open (prevIndex < 0), default to the current desktop row;
            // on subsequent refreshes, preserve the user's selection.
            var defaultIndex = _currentDesktopNavIndex >= 0 ? _currentDesktopNavIndex : 0;
            _navIndex = Math.Clamp(prevIndex < 0 ? defaultIndex : prevIndex, 0, _navItems.Count - 1);
            ApplyNavHighlight();
        }

        // Re-position after layout so the overlay is centered with its new size.
        // WPF hasn't measured the new content yet at this point, so defer.
        if (IsLoaded)
        {
            UpdateLayout();
            PositionOnActiveMonitor();
        }
    }

    /// <summary>
    /// Walks the tree in visual (depth-first) order to build _navItems so that
    /// keyboard Up/Down matches the on-screen order regardless of creation order.
    /// </summary>
    private void CollectNavItems()
    {
        foreach (TreeViewItem root in NetworkTree.Items)
            CollectNavItemsRecursive(root);
    }

    private void CollectNavItemsRecursive(TreeViewItem item)
    {
        Action? action = null;
        bool isCurrentLocal = false;

        if (item.Tag is Action a)
        {
            action = a;
        }
        else if (item.Tag is (Action a2, bool current))
        {
            action = a2;
            isCurrentLocal = current;
        }

        if (action != null)
        {
            _navItems.Add((item, action));
            if (isCurrentLocal)
                _currentDesktopNavIndex = _navItems.Count - 1;
        }

        foreach (TreeViewItem child in item.Items)
            CollectNavItemsRecursive(child);
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

        var localApiDesktops = VirtualDesktopProvider.GetAllDesktops();
        _localApiDesktops    = localApiDesktops;   // cache for GetDesktopRowsFor()
        _cachedDesktopCount     = localApiDesktops.Length;
        _cachedDesktopSignature = BuildDesktopSignature(localApiDesktops);
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
            // Merge msrdc (Cloud DevBox / AVD) windows by pairing them with peers
            // via MachineName match, RdpClientName match, or DNS/IP resolution.
            MainWindow.MergeMsrdcDesktopEntries(localRdpMap, _peers, _localMachineName);

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

        if (localRdpMap.Count > 0)
            AppLogger.Log($"BuildTree: hostedMap has {localRdpMap.Count} entries: {string.Join(", ", localRdpMap.Select(kv => $"{kv.Key}→desktop{kv.Value}"))}");
        else
            AppLogger.Log($"BuildTree: hostedMap is EMPTY — no mstsc/msrdc windows found for peers [{string.Join(", ", allMachineNames)}]");

        // Only include peers that are part of the desktop map:
        // - RDP servers the local machine is hosting an mstsc/msrdc window for
        // - Client peers that host servers we know about
        // - When we're a server: the client peer that hosts us
        // Exclude peers that are merely connected but have no RDP relationship.
        var all = new List<MachineInfo> { localMachineInfo };
        all.AddRange(_peers.Where(p =>
            // Server peers that appear in our local RDP hosted map
            (p.IsRdpServer && localMachineInfo.RdpHostedServers.ContainsKey(
                MachineInfo.NormalizeHostname(p.MachineName))) ||
            // Client peers that host at least one server (including us when we're a server)
            (!p.IsRdpServer && p.RdpHostedServers.Count > 0) ||
            // When we're a server: any client peer that lists us in RdpPeers
            (_localIsRdpServer && !p.IsRdpServer && p.RdpPeers.Any(rp =>
                MachineInfo.NormalizeHostname(rp)
                    .Equals(MachineInfo.NormalizeHostname(_localMachineName), StringComparison.OrdinalIgnoreCase)))
        ));

        AppLogger.Log($"BuildTree: {all.Count} machines in tree: [{string.Join(", ", all.Select(m => $"{m.MachineName}(server={m.IsRdpServer},conn={m.IsConnected})"))}]");

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
            AddDesktopChildrenWithNestedServers(clientItem, client, all, shown, localServerNode: null);
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

    private static readonly System.Windows.Media.Brush _hoverBrush = CreateFrozenBrush(Color.FromArgb(30, 255, 255, 255));
    private static SolidColorBrush CreateFrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Adds a subtle background highlight on mouse hover to a row panel.</summary>
    private static void AddHoverEffect(System.Windows.Controls.Panel panel)
    {
        panel.MouseEnter += (_, _) => panel.Background = _hoverBrush;
        panel.MouseLeave += (_, _) => panel.Background = Brushes.Transparent;
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

        var displayName = !string.IsNullOrEmpty(info.DevBoxFriendlyName)
            ? $"{info.DevBoxFriendlyName} ({info.MachineName})"
            : info.MachineName;

        var nameBlock = new TextBlock
        {
            Text              = displayName,
            Foreground        = info.IsConnected
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x6A)),
            FontSize          = 14,
            FontWeight        = FontWeights.SemiBold,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(dot);
        topRow.Children.Add(nameBlock);

        var leftStack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        leftStack.Children.Add(topRow);

        var header = new DockPanel
        {
            LastChildFill = true,
            Margin        = new Thickness(0, 5, 0, 5),
            Cursor        = Cursors.Hand,
            Background    = Brushes.Transparent  // ensures entire area is hit-testable
        };
        header.Children.Add(leftStack);

        // Clicking anywhere on the machine row triggers Task View.
        var machineName = info.MachineName;
        Action action = () => _onTaskViewRequested(machineName);
        header.MouseLeftButtonDown += (_, e) => { action(); e.Handled = true; };
        AddHoverEffect(header);

        var item = new TreeViewItem { Header = header, IsExpanded = true, Tag = action };
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
                new DesktopRow(d.Index, d.DisplayName, d.IsCurrent, d.Id, d.WallpaperPath, null, d.BackgroundColor, m.MachineName, true)
            ).ToArray();
        }
        if (m.TotalDesktops <= 0) return Array.Empty<DesktopRow>();
        var rows = new DesktopRow[m.TotalDesktops];
        for (int i = 0; i < m.TotalDesktops; i++)
        {
            var name  = i < m.DesktopNames.Count        ? m.DesktopNames[i]        : $"Desktop {i + 1}";
            var thumb = i < m.WallpaperThumbnails.Count ? m.WallpaperThumbnails[i] : null;
            if (string.IsNullOrEmpty(thumb)) thumb = null;
            var color = i < m.WallpaperColors.Count     ? m.WallpaperColors[i]     : null;
            if (string.IsNullOrEmpty(color)) color = null;
            // A remote desktop is only "current" (shows Active, Switch disabled) when the
            // parent context is also active — i.e. the full path from root is currently displayed.
            var isCurrent = parentIsActive && m.CurrentDesktop == i + 1;
            rows[i] = new DesktopRow(i + 1, name, isCurrent, null, null, thumb, color, m.MachineName, false);
        }
        return rows;
    }

    /// Adds desktop rows to <paramref name="clientItem"/> and, for each desktop,
    /// nests any server whose mstsc window lives there according to the client's
    /// <see cref="MachineInfo.RdpHostedServers"/> map.Works identically whether
    /// the client is the local machine or a remote peer — both carry the same map.
    /// <paramref name="localServerNode"/> is non-null when we are the server: it is
    /// the pre-built local-machine node that should be nested on the correct desktop.
    /// </summary>
    private void AddDesktopChildrenWithNestedServers(
        TreeViewItem clientItem, MachineInfo client,
        List<MachineInfo> all, HashSet<string> shown,
        TreeViewItem? localServerNode,
        bool parentIsActive = true)
    {
        var desktops = GetDesktopRowsFor(client, parentIsActive);
        if (desktops.Length == 0) return;

        // Group machines-with-mstsc-windows by the local desktop index reported in the
        // hosted map.  hostedMap keys are always normalized short names; MachineName may be
        // a FQDN, so normalize before lookup.
        // NOTE: we intentionally do NOT filter by IsRdpServer here — a peer can have an
        // mstsc window pointing to it while its RemoteFlattener instance is running in the
        // physical console session (SM_REMOTESESSION=0), causing it to self-report
        // IsRdpServer=false even though this machine is actively connecting to it via RDP.
        // The hostedMap itself (built from the live window scan) is the authoritative source.
        var hostedMap = client.RdpHostedServers;
        var serversByDesktop = all
            .Where(m => !m.MachineName.Equals(_localMachineName, StringComparison.OrdinalIgnoreCase) &&
                        hostedMap.ContainsKey(MachineInfo.NormalizeHostname(m.MachineName)))
            .GroupBy(m => hostedMap[MachineInfo.NormalizeHostname(m.MachineName)])
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
                    // Recurse: s may itself be a client that hosts further servers
                    // (multi-hop chain). parentIsActive flows down so "Active"/Switch
                    // states are only shown when the full path from root is visible.
                    AddDesktopChildrenWithNestedServers(sItem, s, all, shown,
                        localServerNode: null, parentIsActive: d.IsCurrent);
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

            if (bmp != null)
            {
                thumbnail = new Border
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
                };
            }
            else if (!string.IsNullOrEmpty(d.BackgroundColor) && TryParseHexColor(d.BackgroundColor, out var bgColor))
            {
                thumbnail = new Border
                {
                    Width             = 120,
                    Height            = 68,
                    Background        = new SolidColorBrush(bgColor),
                    CornerRadius      = new CornerRadius(4),
                    BorderBrush       = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                    BorderThickness   = new Thickness(1),
                    Margin            = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                thumbnail = MakeThumbnailPlaceholder();
            }
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
        var isCurrent    = d.IsCurrent;

        Action action = () =>
        {
            if (isCurrent) return;
            // If this desktop belongs to a remote server, also switch the local machine
            // to whichever desktop hosts that server's mstsc window.
            if (!isLocal && _rdpDesktopMap.TryGetValue(MachineInfo.NormalizeHostname(machineName), out var localIdx))
                _onSwitchToDesktop(_localMachineName, localIdx);
            _onSwitchToDesktop(machineName, desktopIndex);
        };

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin        = new Thickness(0, 3, 0, 3),
            Cursor        = isCurrent ? null : Cursors.Hand,
            Background    = Brushes.Transparent  // ensures entire area is hit-testable
        };
        row.Children.Add(thumbnail);
        row.Children.Add(textStack);

        // Clicking anywhere on the row switches to that desktop.
        row.MouseLeftButtonDown += (_, e) => { action(); e.Handled = true; };
        if (!isCurrent)
            AddHoverEffect(row);

        var item = new TreeViewItem { Header = row, IsExpanded = false, Tag = (action, isCurrent && isLocal) };
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

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex)) return false;
        if (hex[0] == '#') hex = hex[1..];
        if (hex.Length != 6) return false;
        if (byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = Color.FromRgb(r, g, b);
            return true;
        }
        return false;
    }

    // ── Close / keyboard ─────────────────────────────────────────────────────

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        Close();
        _onSettingsRequested();
    }

    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        AppLogger.Log("TreeWindow.OnClosing");
        // Unhook immediately so Deactivated (which fires during Close)
        // cannot re-enter Close().
        Deactivated -= Window_Deactivated;
        _closing = true;
        base.OnClosing(e);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_closing) return;
        AppLogger.Log("TreeWindow: closing on deactivation.");
        _closing = true;
        Close();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Hook the close-on-deactivate only after the window has been
        // activated for the first time. This avoids a race where the
        // Deactivated event fires before the window ever gains focus
        // (e.g. during a Win+Tab hotkey transition).
        Deactivated -= Window_Deactivated;
        Deactivated += Window_Deactivated;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _closing = true;
                Close();
                break;

            case Key.Up:
                if (_navItems.Count > 0)
                {
                    _navKeyActive = true;
                    _navIndex = _navIndex <= 0 ? _navItems.Count - 1 : _navIndex - 1;
                    ApplyNavHighlight();
                    _navKeyActive = false;
                    e.Handled = true;
                }
                break;

            case Key.Down:
                if (_navItems.Count > 0)
                {
                    _navKeyActive = true;
                    _navIndex = _navIndex >= _navItems.Count - 1 ? 0 : _navIndex + 1;
                    ApplyNavHighlight();
                    _navKeyActive = false;
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (_navIndex >= 0 && _navIndex < _navItems.Count)
                {
                    _navItems[_navIndex].Action();
                    e.Handled = true;
                }
                break;
        }
    }
}
