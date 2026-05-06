using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Diagnostics;
using System.Windows.Interop;
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
                if (!Connections.Any(m => m.MachineName.Equals(name, StringComparison.OrdinalIgnoreCase)))
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
            if (!Connections.Any(m => m.MachineName.Equals(peer.MachineName, StringComparison.OrdinalIgnoreCase)))
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
        if (!Connections.Any(m => m.MachineName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Connections.Add(new MachineInfo { MachineName = name });
            SaveSettings();
        }
        AddPeerBox.Clear();
    }

    private void RemovePeer_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as System.Windows.Controls.Button)?.Tag as string;
        if (string.IsNullOrEmpty(name)) return;
        var entry = Connections.FirstOrDefault(m =>
            m.MachineName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            Connections.Remove(entry);
            SaveSettings();
        }
    }

    private void PeersToggle_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = PeersSection.Visibility == Visibility.Collapsed;
        PeersSection.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        PeersChevron.Text       = collapsed ? "▾" : "▸";
    }

    private void PasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = PasswordSection.Visibility == Visibility.Collapsed;
        PasswordSection.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        PasswordChevron.Text       = collapsed ? "▾" : "▸";
    }

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = LogBox.Visibility == Visibility.Collapsed;
        LogBox.Visibility  = collapsed ? Visibility.Visible : Visibility.Collapsed;
        LogChevron.Text    = collapsed ? "▾" : "▸";
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
            // Still send an initial state after 1 s so peers see us right away.
            _stateTimer = new Timer(_ =>
            {
                BroadcastOurState();
                _stateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }, null, 1_000, Timeout.Infinite);
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

            // Trim to MaxLogLines to avoid unbounded growth.
            var text = LogBox.Text;
            var lines = text.Split('\n');
            if (lines.Length > MaxLogLines)
                LogBox.Text = string.Join('\n', lines[^MaxLogLines..]);

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
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0xD0, 0x6A));
            StatusPill.Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x1E, 0x35, 0x1E));
            StartButton.Content = "■  Stop";
            StartButton.Style   = (Style)FindResource("DangerButton");
        }
        else
        {
            StatusLabel.Text       = "● Stopped";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x80));
            StatusPill.Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x38));
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
            var info = Connections.FirstOrDefault(m =>
                m.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase));
            if (info != null)
                info.IsConnected = false;
            RefreshStatusLabel();
        });
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
                    _networkManager?.LocalMachineName ?? Environment.MachineName,
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

        var localName = _networkManager?.LocalMachineName ?? Environment.MachineName;
        if (machineName.Equals(localName, StringComparison.OrdinalIgnoreCase))
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
        var localName = _networkManager?.LocalMachineName ?? Environment.MachineName;
        if (machineName.Equals(localName, StringComparison.OrdinalIgnoreCase))
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
        info.RdpHostedServers    = msg.RdpHostedServers != null
            ? new Dictionary<string, int>(
                msg.RdpHostedServers.ToDictionary(
                    kv => MachineInfo.NormalizeHostname(kv.Key),
                    kv => kv.Value),
                StringComparer.OrdinalIgnoreCase)
            : new();
        info.DesktopNames        = msg.DesktopNames        ?? new();
        info.WallpaperThumbnails = msg.WallpaperThumbnails ?? new();

        // Ensure every machine the sender knows about appears in our list.
        // We only add entries — we never downgrade a directly-connected peer to offline.
        var localName = _networkManager?.LocalMachineName ?? Environment.MachineName;
        foreach (var peer in info.RdpPeers)
        {
            if (string.IsNullOrWhiteSpace(peer)) continue;
            if (peer.Equals(localName, StringComparison.OrdinalIgnoreCase)) continue;
            var peerInfo = GetOrAdd(peer);
            // Only mark as indirect if not already directly connected.
            if (!peerInfo.IsConnected)
                peerInfo.IsIndirect = true;
        }
        RefreshStatusLabel();
    }

    private MachineInfo GetOrAdd(string machineName)
    {
        var normalized = MachineInfo.NormalizeHostname(machineName);
        var info = Connections.FirstOrDefault(m =>
            m.MachineName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (info == null)
        {
            info = new MachineInfo { MachineName = normalized };
            Connections.Add(info);
        }
        return info;
    }

    private void BroadcastOurState()
    {
        if (_networkManager == null) return;

        // Thumbnail encoding requires a UI thread (BitmapImage is a DispatcherObject).
        // InvokeAsync posts to the UI message queue from any thread safely.
        Dispatcher.InvokeAsync(() =>
        {
            if (_networkManager == null) return;
            var apiDesktops = VirtualDesktopProvider.GetAllDesktops();
            var msg = new NetworkMessage
            {
                Type                = MessageTypes.StateUpdate,
                MachineName         = _networkManager.LocalMachineName,
                CurrentDesktop      = VirtualDesktopHelper.GetCurrentDesktopIndex(),
                TotalDesktops       = VirtualDesktopHelper.GetTotalDesktopCount(),
                IsRdpServer         = _isRdpServer,
                RdpPeers            = _networkManager.ConnectedPeers.ToList(),
                RdpHostedServers    = _isRdpServer ? null
                    : RdpWindowLocator.GetRdpDesktopMap(_networkManager.ConnectedPeers.ToList())
                        .ToDictionary(
                            kv => MachineInfo.NormalizeHostname(kv.Key),
                            kv => kv.Value,
                            StringComparer.OrdinalIgnoreCase),
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
