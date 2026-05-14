using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteFlattener.Hotkeys;
using RemoteFlattener.Logging;
using RemoteFlattener.Models;
using RemoteFlattener.Network;
using RemoteFlattener.RDP;
using RemoteFlattener.Settings;
using RemoteFlattener.VirtualDesktop;
using System.Text;

namespace RemoteFlattener;

public partial class MainWindow : Window
{
    private NetworkManager? _networkManager;
    private HotkeyManager? _hotkeyManager;
    private TreeWindow? _treeWindow;
    private Timer? _stateTimer;

    private bool _isRunning;
    private bool _isRdpServer;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    // Prevents the server from re-broadcasting a switch that echoes back as a
    // synthetic keystroke from the client's SendInput call.
    private long _lastSwitchBroadcastTick;
    private const long SwitchBroadcastCooldownMs = 1500;

    /// <summary>Bound to the ConnectionList ListBox.</summary>
    public ObservableCollection<MachineInfo> Connections { get; } = new();

    private const int MaxLogLines = 500;
    private int _logLineCount;

    // ── Static frozen brushes for status label (avoid per-call allocations) ───
    private static readonly SolidColorBrush RunningForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0xD0, 0x6A)));
    private static readonly SolidColorBrush RunningBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x35, 0x1E)));
    private static readonly SolidColorBrush StoppedForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x80)));
    private static readonly SolidColorBrush StoppedBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x38)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>
    /// The local machine name from the network manager (if running) or the environment.
    /// </summary>
    private string LocalName => _networkManager?.LocalMachineName ?? Environment.MachineName;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        AppLogger.LogWritten += OnLogWritten;
        AppLogger.Log($"RemoteFlattener started.  Local machine: {Environment.MachineName}");

        var ver = System.Reflection.Assembly.GetExecutingAssembly()
                      .GetName().Version;
        VersionLabel.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : string.Empty;

        InitializeTrayIcon();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = SettingsStore.Load();

        // Restore machines list — pre-populate Connections so the merged peer list is visible
        // immediately.  Each entry starts as IsConnected=false until a peer actually connects.
        if (!string.IsNullOrWhiteSpace(s.Machines))
        {
            foreach (var name in s.Machines
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FindPeer(name) == null)
                    Connections.Add(new MachineInfo { MachineName = name });
            }
        }

        // Restore or auto-generate password.
        var savedPassword = string.IsNullOrWhiteSpace(s.EncryptedPassword)
            ? null
            : SettingsStore.Decrypt(s.EncryptedPassword);

        if (!string.IsNullOrEmpty(savedPassword))
        {
            PasswordBox.Text = savedPassword;
            AppLogger.Log("Password loaded from saved settings — auto-starting.");
        }
        else
        {
            GenerateAndSavePassword();
            AppLogger.Log("No saved password found — generated a new one.");
        }

        // Save whenever the user edits the password box.
        PasswordBox.TextChanged += (_, _) => SaveSettings();

        // Auto-start only when we had a saved password (i.e. user previously configured the app).
        // Use Loaded to ensure all UI controls are fully initialised first.
        if (!string.IsNullOrEmpty(savedPassword))
            Loaded += (_, _) => StartNetwork();
    }

    private void TitleLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "RemoteFlattener",
            Icon    = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open",  null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("-");
        menu.Items.Add("Exit",  null, (_, _) => Dispatcher.Invoke(() =>
        {
            _notifyIcon.Visible = false;
            Application.Current.Shutdown();
        }));
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick     += (_, _) => Dispatcher.Invoke(OpenSettings);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
        base.OnStateChanged(e);
    }

    private void GenerateAndSavePassword()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        PasswordBox.Text = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
        // TextChanged fires and calls SaveSettings automatically.
    }

    private void SaveSettings()
    {
        var password = PasswordBox.Text ?? string.Empty;
        SettingsStore.Save(new SettingsStore.AppSettings
        {
            EncryptedPassword = string.IsNullOrEmpty(password) ? string.Empty : SettingsStore.Encrypt(password),
            Machines = string.Join("\n", Connections.Select(m => m.MachineName))
        });
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void DetectMachines_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log("Detecting RDP peers from active TCP connections on port 3389...");
        var peers = RdpConnectionDetector.GetRdpPeers();
        if (peers.Count == 0)
        {
            AppLogger.Log("Detect: no active RDP connections found.");
            ShowPeersStatus("No active RDP connections found.");
            return;
        }

        int added = 0;
        foreach (var peer in peers)
        {
            if (FindPeer(peer.MachineName) == null)
            {
                Connections.Add(new MachineInfo { MachineName = peer.MachineName });
                added++;
                // Start a connector immediately using the known-good IP address so that
                // cross-domain short-name DNS failures don't prevent the connection.
                _networkManager?.ConnectToPeer(peer.MachineName, peer.ConnectionAddress);
            }
        }

        AppLogger.Log($"Detect: found {peers.Count} peer(s), added {added} new.");
        ShowPeersStatus(added > 0 ? $"Added {added} new peer(s)." : "No new peers found.");
        if (added > 0) SaveSettings();
    }

    private System.Threading.Timer? _peersStatusTimer;
    private void ShowPeersStatus(string message)
    {
        PeersStatusText.Text       = message;
        PeersStatusText.Visibility = Visibility.Visible;
        _peersStatusTimer?.Dispose();
        _peersStatusTimer = new System.Threading.Timer(_ =>
            Dispatcher.Invoke(() => PeersStatusText.Visibility = Visibility.Collapsed),
            null, 3000, System.Threading.Timeout.Infinite);
    }

    private void AddPeer_Click(object sender, RoutedEventArgs e) => AddPeerFromBox();

    private void AddPeerBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AddPeerFromBox();
    }

    private void AddPeerFromBox()
    {
        var name = AddPeerBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return;
        if (FindPeer(name) == null)
        {
            Connections.Add(new MachineInfo { MachineName = name });
            SaveSettings();
        }
        AddPeerBox.Clear();
    }

    private void RemovePeer_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as Button)?.Tag as string;
        if (string.IsNullOrEmpty(name)) return;
        var entry = FindPeer(name);
        if (entry != null)
        {
            Connections.Remove(entry);
            SaveSettings();
        }
    }

    private void PeersToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleSection(PeersSection, PeersChevron);

    private void PasswordToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleSection(PasswordSection, PasswordChevron);

    private void LogToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleSection(LogBox, LogChevron);

    private static void ToggleSection(UIElement section, System.Windows.Controls.TextBlock chevron)
    {
        var collapsed = section.Visibility == Visibility.Collapsed;
        section.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text       = collapsed ? "▾" : "▸";
    }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        GenerateAndSavePassword();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartNetwork();

    private void StartNetwork()
    {
        var password = PasswordBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Please enter or generate a password first.",
                "RemoteFlattener", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var machines = Connections.Select(m => m.MachineName).ToArray();

        _isRdpServer = RdpRoleDetector.IsRemoteSession();
        _isRunning   = true;

        AppLogger.Log($"Starting.  Role: {(_isRdpServer ? "RDP Server" : "RDP Client")}");
        if (machines.Length > 0)
            AppLogger.Log($"Configured peers: {string.Join(", ", machines)}");
        else
            AppLogger.Log("No peers configured — listening only.");

        NetworkTreeButton.IsEnabled = true;
        RefreshStatusLabel();

        // Initialise the VirtualDesktop COM API (falls back gracefully if unavailable).
        VirtualDesktopProvider.TryInitialize();

        // Start networking.
        _networkManager = new NetworkManager();
        _networkManager.MessageReceived  += OnMessageReceived;
        _networkManager.PeerConnected    += OnPeerConnected;
        _networkManager.PeerDisconnected += OnPeerDisconnected;
        _networkManager.Start(password, machines);

        // When the COM API is available, broadcast immediately whenever the desktop changes.
        // Otherwise fall back to a 5-second polling timer.
        if (VirtualDesktopProvider.IsAvailable)
        {
            AppLogger.Log("Using VirtualDesktop API change events for state broadcasting.");
            VirtualDesktopProvider.DesktopChanged += OnDesktopChangedEvent;
            // Send an initial state after 1 s so peers see us right away,
            // then re-broadcast every 30 s to catch mstsc window position changes
            // (no Windows event exists for windows moving between virtual desktops).
            _stateTimer = new Timer(_ => BroadcastOurState(), null, 1_000, 30_000);
        }
        else
        {
            AppLogger.Log("VirtualDesktop API unavailable — using 5 s polling for state broadcasting.");
            _stateTimer = new Timer(_ => BroadcastOurState(), null, 1_000, 5_000);
        }

        // Install keyboard hook — Win+Tab on all machines; switch hotkeys only on server.
        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.WinTabPressed += OnWinTabPressed;
        if (_isRdpServer)
        {
            _hotkeyManager.SwitchDesktopLeft  += OnSendSwitchLeft;
            _hotkeyManager.SwitchDesktopRight += OnSendSwitchRight;
            AppLogger.Log("Hotkey hook installed (Win+Tab overlay + Ctrl+Win+Left/Right broadcast).");
        }
        else
        {
            AppLogger.Log("Running as RDP Client — hotkey hook installed for Win+Tab overlay only.");
        }
        _hotkeyManager.Install();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log("Stopping.");
        StopAll();
        NetworkTreeButton.IsEnabled = false;
        RefreshStatusLabel();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void StopAll()
    {
        _isRunning = false;

        VirtualDesktopProvider.DesktopChanged -= OnDesktopChangedEvent;

        _stateTimer?.Dispose();
        _stateTimer = null;

        _hotkeyManager?.Uninstall();
        _hotkeyManager = null;

        _networkManager?.Stop();
        _networkManager?.Dispose();
        _networkManager = null;

        _treeWindow?.Close();
        _treeWindow = null;

        // Mark all peers as disconnected but keep them in the list so the user can see
        // their configured peers and restart without re-entering them.
        foreach (var peer in Connections)
        {
            peer.IsConnected    = false;
            peer.IsIndirect     = false;
            peer.CurrentDesktop = 0;
            peer.TotalDesktops  = 0;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Log("Application closed.");
        AppLogger.LogWritten -= OnLogWritten;
        if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
        SaveSettings();
        StopAll();
        base.OnClosed(e);
    }

    private void OnLogWritten(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            _logLineCount++;

            // Trim in bulk when we exceed the threshold, cutting back to half
            // to avoid re-trimming on every subsequent line.
            if (_logLineCount > MaxLogLines)
            {
                var text = LogBox.Text;
                int linesToRemove = _logLineCount - MaxLogLines / 2;
                int cutIndex = 0;
                for (int i = 0; i < linesToRemove && cutIndex < text.Length; i++)
                {
                    int nl = text.IndexOf('\n', cutIndex);
                    if (nl < 0) break;
                    cutIndex = nl + 1;
                }
                if (cutIndex > 0 && cutIndex < text.Length)
                {
                    LogBox.Text = text[cutIndex..];
                    _logLineCount -= linesToRemove;
                }
            }

            LogBox.ScrollToEnd();
        });
    }

    private void NetworkTree_Click(object sender, RoutedEventArgs e) =>
        OnWinTabPressed();

    private void OpenSettings()
    {
        // Bring the settings window to the front.
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = AppLogger.LogFilePath;
        if (path == null)
        {
            MessageBox.Show("No log file has been created yet.",
                "RemoteFlattener", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file:\n{ex.Message}",
                "RemoteFlattener", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Desktop change event ──────────────────────────────────────────────────

    private void OnDesktopChangedEvent()
    {
        AppLogger.Log("Desktop changed (VirtualDesktop API event) — broadcasting state.");
        BroadcastOurState();
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void RefreshStatusLabel()
    {
        var connected = Connections.Count(m => m.IsConnected || m.IsIndirect);
        if (_isRunning)
        {
            StatusLabel.Text       = connected > 0 ? $"● Running · {connected} peer{(connected == 1 ? "" : "s")}" : "● Running";
            StatusLabel.Foreground = RunningForeground;
            StatusPill.Background  = RunningBackground;
            StartButton.Content = "■  Stop";
            StartButton.Style   = (Style)FindResource("DangerButton");
        }
        else
        {
            StatusLabel.Text       = "● Stopped";
            StatusLabel.Foreground = StoppedForeground;
            StatusPill.Background  = StoppedBackground;
            StartButton.Content = "▶  Start";
            StartButton.Style   = (Style)FindResource("PrimaryButton");
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) Stop_Click(sender, e);
        else Start_Click(sender, e);
    }

    // ── Network callbacks (arrive on background threads) ─────────────────────

    private void OnMessageReceived(string machineName, NetworkMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            switch (msg.Type)
            {
                case MessageTypes.StateUpdate:
                    // Use msg.MachineName (the originator) not machineName (the TCP relay hop).
                    // If a STATE_UPDATE from C is relayed through B, machineName would be "B",
                    // which would overwrite B's entry with C's desktop/wallpaper data.
                    UpsertMachineInfo(msg.MachineName ?? machineName, msg);
                    break;

                // Only non-server machines act on desktop-switch commands.
                case MessageTypes.SwitchLeft when !_isRdpServer:
                    AppLogger.Log($"Received {MessageTypes.SwitchLeft} from {machineName} — switching local desktop left.");
                    VirtualDesktopSwitcher.SwitchLeft(new WindowInteropHelper(this).Handle);
                    break;

                case MessageTypes.SwitchRight when !_isRdpServer:
                    AppLogger.Log($"Received {MessageTypes.SwitchRight} from {machineName} — switching local desktop right.");
                    VirtualDesktopSwitcher.SwitchRight(new WindowInteropHelper(this).Handle);
                    break;

                case MessageTypes.TaskView:
                    AppLogger.Log($"Received {MessageTypes.TaskView} from {machineName} — invoking Task View.");
                    InvokeTaskView();
                    break;

                case MessageTypes.SwitchToDesktop:
                    AppLogger.Log($"Received {MessageTypes.SwitchToDesktop} ({msg.CurrentDesktop}) from {machineName}.");
                    if (!VirtualDesktopProvider.SwitchToIndex(msg.CurrentDesktop))
                        AppLogger.Log("VirtualDesktop API unavailable — cannot switch to specific desktop index.");
                    break;
            }
        });
    }

    private void OnPeerConnected(string machineName)
    {
        Dispatcher.Invoke(() =>
        {
            var info = GetOrAdd(machineName);
            info.IsConnected = true;
            info.IsIndirect  = false;  // now directly connected
            // Share our current state with the newly connected peer.
            BroadcastOurState();
            RefreshStatusLabel();
        });
    }

    private void OnPeerDisconnected(string machineName)
    {
        Dispatcher.Invoke(() =>
        {
            var info = FindPeer(machineName);
            if (info != null)
            {
                info.IsConnected      = false;
                info.RdpPeers         = new();
                info.RdpHostedServers = new();
                info.RdpClientName    = null;
            }

            // Re-evaluate IsIndirect for all peers: a peer should only be
            // indirect if some still-connected peer lists it in its RdpPeers.
            RecalculateIndirectPeers();
            RefreshStatusLabel();
        });
    }

    /// <summary>
    /// Recomputes <see cref="MachineInfo.IsIndirect"/> for every peer based on
    /// the RdpPeers lists of currently-connected peers only.  Peers that were
    /// only reachable through a now-disconnected node become invisible.
    /// </summary>
    private void RecalculateIndirectPeers()
    {
        var indirectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in Connections.Where(p => p.IsConnected))
            foreach (var rp in peer.RdpPeers)
                if (!string.IsNullOrWhiteSpace(rp))
                    indirectNames.Add(MachineInfo.NormalizeHostname(rp));

        // Remove self from the set — we are never "indirect" to ourselves.
        indirectNames.Remove(MachineInfo.NormalizeHostname(LocalName));

        foreach (var peer in Connections)
        {
            // Never mark a directly-connected peer as indirect.
            peer.IsIndirect = !peer.IsConnected && indirectNames.Contains(
                MachineInfo.NormalizeHostname(peer.MachineName));
        }
    }

    // ── Hotkey callbacks (may arrive on thread pool) ──────────────────────────

    private void OnWinTabPressed()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_treeWindow is { IsVisible: true })
            {
                AppLogger.Log("Win+Tab: closing overlay.");
                _treeWindow.Close();
                _treeWindow = null;
            }
            else
            {
                AppLogger.Log("Win+Tab: opening overlay.");
                _treeWindow = new TreeWindow(
                    Connections,
                    LocalName,
                    _isRdpServer,
                    RequestTaskView,
                    RequestSwitchToDesktop,
                    OpenSettings);
                _treeWindow.Closed += (_, _) => _treeWindow = null;
                _treeWindow.Show();
            }
        });
    }

    private void OnSendSwitchLeft()
    {
        if (!TryAcquireSwitchCooldown("LEFT")) return;
        AppLogger.Log($"Broadcasting {MessageTypes.SwitchLeft} to all peers.");
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = MessageTypes.SwitchLeft });
    }

    private void OnSendSwitchRight()
    {
        if (!TryAcquireSwitchCooldown("RIGHT")) return;
        AppLogger.Log($"Broadcasting {MessageTypes.SwitchRight} to all peers.");
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = MessageTypes.SwitchRight });
    }

    private void RequestTaskView(string machineName)
    {
        _treeWindow?.Close();
        _treeWindow = null;

        if (machineName.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log("Task View requested for local machine.");
            InvokeTaskView();
        }
        else
        {
            AppLogger.Log($"Task View requested for {machineName} — sending {MessageTypes.TaskView} message.");
            _ = _networkManager?.SendToPeerAsync(machineName, new NetworkMessage { Type = MessageTypes.TaskView });
        }
    }

    private void RequestSwitchToDesktop(string machineName, int desktopIndex)
    {
        if (machineName.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log($"Switch to desktop {desktopIndex} on local machine.");
            VirtualDesktopProvider.SwitchToIndex(desktopIndex);
        }
        else
        {
            AppLogger.Log($"Requesting desktop {desktopIndex} switch on {machineName}.");
            _ = _networkManager?.SendToPeerAsync(machineName, new NetworkMessage
            {
                Type           = MessageTypes.SwitchToDesktop,
                CurrentDesktop = desktopIndex
            });
        }
    }

    private static void InvokeTaskView()
    {
        try
        {
            AppLogger.Log("Invoking Task View via SendInput (Win+Tab).");
            VirtualDesktopSwitcher.ShowTaskView();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Task View invocation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true and records the broadcast time if the cooldown has elapsed.
    /// Returns false (suppressing the broadcast) if called again within
    /// <see cref="SwitchBroadcastCooldownMs"/> ms — this breaks the feedback loop
    /// caused by the client's SendInput echoing back through the RDP session.
    /// </summary>
    private bool TryAcquireSwitchCooldown(string direction)
    {
        var now  = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSwitchBroadcastTick);
        if (now - last < SwitchBroadcastCooldownMs)
        {
            AppLogger.Log($"Switch {direction} broadcast suppressed — cooldown active ({SwitchBroadcastCooldownMs} ms).");
            return false;
        }
        Interlocked.Exchange(ref _lastSwitchBroadcastTick, now);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpsertMachineInfo(string machineName, NetworkMessage msg)
    {
        var info = GetOrAdd(machineName);
        info.CurrentDesktop      = msg.CurrentDesktop;
        info.TotalDesktops       = msg.TotalDesktops;
        info.IsRdpServer         = msg.IsRdpServer;
        info.IsConnected         = true;
        info.RdpPeers            = msg.RdpPeers            ?? new();
        info.RdpClientName       = msg.RdpClientName;
        info.RdpHostedServers    = NormalizeRdpHostedServers(msg.RdpHostedServers);
        info.DesktopNames        = msg.DesktopNames        ?? new();
        info.WallpaperThumbnails = msg.WallpaperThumbnails ?? new();

        // Ensure every machine the sender knows about appears in our list.
        // We only add entries — we never downgrade a directly-connected peer to offline.
        foreach (var peer in info.RdpPeers)
        {
            if (string.IsNullOrWhiteSpace(peer)) continue;
            if (peer.Equals(LocalName, StringComparison.OrdinalIgnoreCase)) continue;
            var peerInfo = GetOrAdd(peer);
            // Only mark as indirect if not already directly connected.
            if (!peerInfo.IsConnected)
                peerInfo.IsIndirect = true;
        }
        RefreshStatusLabel();
    }

    /// <summary>
    /// Finds an existing peer by normalized hostname, or null if not present.
    /// </summary>
    private MachineInfo? FindPeer(string machineName)
    {
        var normalized = MachineInfo.NormalizeHostname(machineName);
        return Connections.FirstOrDefault(m =>
            MachineInfo.NormalizeHostname(m.MachineName).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private MachineInfo GetOrAdd(string machineName)
    {
        var info = FindPeer(machineName);
        if (info == null)
        {
            info = new MachineInfo { MachineName = machineName };
            Connections.Add(info);
        }
        return info;
    }

    /// <summary>
    /// Normalizes an RdpHostedServers dictionary so all keys use canonical hostnames.
    /// Returns an empty dictionary if the input is null.
    /// </summary>
    private static Dictionary<string, int> NormalizeRdpHostedServers(Dictionary<string, int>? raw)
    {
        if (raw == null || raw.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, int>(
            raw.ToDictionary(
                kv => MachineInfo.NormalizeHostname(kv.Key),
                kv => kv.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the local RdpHostedServers map by scanning mstsc windows (hostname-based)
    /// and msrdc windows (paired with DevBox/AVD peers via <see cref="MachineInfo.RdpClientName"/>).
    /// </summary>
    private Dictionary<string, int> BuildLocalRdpHostedServers()
    {
        var connectedPeers = _networkManager!.ConnectedPeers.ToList();
        var map = RdpWindowLocator.GetRdpDesktopMap(connectedPeers);
        MergeMsrdcDesktopEntries(map, Connections, LocalName);
        return NormalizeRdpHostedServers(map);
    }

    /// <summary>
    /// Pairs msrdc.exe windows (Cloud DevBox / AVD) with server peers that report
    /// <see cref="MachineInfo.RdpClientName"/> matching <paramref name="localMachineName"/>.
    /// Matched entries are added to <paramref name="rdpDesktopMap"/> keyed by the
    /// server's hostname.
    /// </summary>
    internal static void MergeMsrdcDesktopEntries(
        Dictionary<string, int> rdpDesktopMap,
        IEnumerable<MachineInfo> peers,
        string localMachineName)
    {
        MergeMsrdcDesktopEntries(rdpDesktopMap, peers, localMachineName,
            RdpWindowLocator.GetMsrdcDesktopMap);
    }

    /// <summary>
    /// Testable overload: accepts a factory for the msrdc window map so tests can
    /// inject known window data without live window enumeration.
    /// </summary>
    internal static void MergeMsrdcDesktopEntries(
        Dictionary<string, int> rdpDesktopMap,
        IEnumerable<MachineInfo> peers,
        string localMachineName,
        Func<Dictionary<string, int>> getMsrdcDesktopMap)
    {
        var normalizedLocal = MachineInfo.NormalizeHostname(localMachineName);

        // Find DevBox/AVD peers: server-role peers whose RdpClientName matches us.
        var devBoxPeers = peers
            .Where(p => p.IsRdpServer &&
                        !string.IsNullOrEmpty(p.RdpClientName) &&
                        MachineInfo.NormalizeHostname(p.RdpClientName!)
                            .Equals(normalizedLocal, StringComparison.OrdinalIgnoreCase))
            .Where(p => !rdpDesktopMap.ContainsKey(MachineInfo.NormalizeHostname(p.MachineName)))
            .ToList();

        if (devBoxPeers.Count == 0) return;

        var msrdcMap = getMsrdcDesktopMap();
        if (msrdcMap.Count == 0) return;

        // Remove msrdc entries that are already accounted for in the mstsc-based map
        // (unlikely but defensive).
        foreach (var existing in rdpDesktopMap.Keys.ToList())
        {
            msrdcMap.Remove(existing);
        }

        if (msrdcMap.Count == 0) return;

        // Pair: if there's exactly one DevBox peer and one unmatched msrdc window,
        // they must correspond.  With multiple of each, pair by order (best-effort).
        var msrdcEntries = msrdcMap.ToList();
        for (int i = 0; i < devBoxPeers.Count && i < msrdcEntries.Count; i++)
        {
            var peerName = MachineInfo.NormalizeHostname(devBoxPeers[i].MachineName);
            rdpDesktopMap[peerName] = msrdcEntries[i].Value;
        }
    }

    private void BroadcastOurState()
    {
        if (_networkManager == null) return;

        // Thumbnail encoding requires a UI thread (BitmapImage is a DispatcherObject).
        // InvokeAsync posts to the UI message queue from any thread safely.
        Dispatcher.InvokeAsync(() =>
        {
            if (_networkManager == null) return;

            // Re-evaluate RDP role each cycle so the tree updates when a
            // remote-desktop session ends (e.g. the client laptop goes offline).
            _isRdpServer = RdpRoleDetector.IsRemoteSession();

            var apiDesktops = VirtualDesktopProvider.GetAllDesktops();
            var msg = new NetworkMessage
            {
                Type                = MessageTypes.StateUpdate,
                MachineName         = _networkManager.LocalMachineName,
                CurrentDesktop      = VirtualDesktopHelper.GetCurrentDesktopIndex(),
                TotalDesktops       = VirtualDesktopHelper.GetTotalDesktopCount(),
                IsRdpServer         = _isRdpServer,
                RdpPeers            = _networkManager.ConnectedPeers.ToList(),
                RdpClientName       = _isRdpServer ? RdpConnectionDetector.GetRdpClientName() : null,
                RdpHostedServers    = _isRdpServer ? null
                    : BuildLocalRdpHostedServers(),
                DesktopNames        = apiDesktops.Select(d => d.DisplayName).ToList(),
                WallpaperThumbnails = apiDesktops.Select(d => EncodeWallpaperThumbnail(d.WallpaperPath) ?? "").ToList()
            };
            _ = _networkManager.BroadcastAsync(msg);
        });
    }

    private static string? EncodeWallpaperThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            BitmapImage bmp;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource     = stream;
                bmp.DecodePixelWidth = 128;
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            bmp.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 60 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}
