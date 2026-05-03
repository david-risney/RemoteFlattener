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
        var peers = RdpConnectionDetector.GetRdpPeerHostnames();
        if (peers.Count == 0)
        {
            AppLogger.Log("Detect: no active RDP connections found.");
            MessageBox.Show("No active RDP connections found on port 3389.",
                "RemoteFlattener", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppLogger.Log($"Detect: found {peers.Count} peer(s): {string.Join(", ", peers)}");

        // Add newly-detected hosts that are not already in the list.
        foreach (var peer in peers)
        {
            if (!Connections.Any(m => m.MachineName.Equals(peer, StringComparison.OrdinalIgnoreCase)))
                Connections.Add(new MachineInfo { MachineName = peer });
        }
        SaveSettings();
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

        StartButton.IsEnabled        = false;
        StopButton.IsEnabled          = true;
        NetworkTreeButton.IsEnabled   = true;
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
        StartButton.IsEnabled        = true;
        StopButton.IsEnabled         = false;
        NetworkTreeButton.IsEnabled  = false;
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
            peer.CurrentDesktop = 0;
            peer.TotalDesktops  = 0;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Log("Application closed.");
        AppLogger.LogWritten -= OnLogWritten;
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
        if (_isRunning)
        {
            var role = _isRdpServer ? "RDP Server" : "RDP Client";
            StatusLabel.Text       = $"● Running – {role}";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0xD0, 0x6A));
            StatusPill.Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x1E, 0x35, 0x1E));
            RoleLabel.Text = role;
        }
        else
        {
            StatusLabel.Text       = "● Stopped";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0x9A, 0x5A));
            StatusPill.Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x3A, 0x2A));
            RoleLabel.Text = "Virtual desktop sync over RDP";
        }
    }

    // ── Network callbacks (arrive on background threads) ─────────────────────

    private void OnMessageReceived(string machineName, NetworkMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            switch (msg.Type)
            {
                case "STATE_UPDATE":
                    UpsertMachineInfo(machineName, msg);
                    break;

                // Only non-server machines act on desktop-switch commands.
                case "SWITCH_DESKTOP_LEFT" when !_isRdpServer:
                    AppLogger.Log($"Received SWITCH_DESKTOP_LEFT from {machineName} — switching local desktop left.");
                    VirtualDesktopSwitcher.SwitchLeft(new WindowInteropHelper(this).Handle);
                    break;

                case "SWITCH_DESKTOP_RIGHT" when !_isRdpServer:
                    AppLogger.Log($"Received SWITCH_DESKTOP_RIGHT from {machineName} — switching local desktop right.");
                    VirtualDesktopSwitcher.SwitchRight(new WindowInteropHelper(this).Handle);
                    break;

                case "TASK_VIEW":
                    AppLogger.Log($"Received TASK_VIEW from {machineName} — invoking Task View.");
                    InvokeTaskView();
                    break;

                case "SWITCH_TO_DESKTOP_INDEX":
                    AppLogger.Log($"Received SWITCH_TO_DESKTOP_INDEX ({msg.CurrentDesktop}) from {machineName}.");
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
            // Share our current state with the newly connected peer.
            BroadcastOurState();
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
        AppLogger.Log("Broadcasting SWITCH_DESKTOP_LEFT to all peers.");
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = "SWITCH_DESKTOP_LEFT" });
    }

    private void OnSendSwitchRight()
    {
        if (!TryAcquireSwitchCooldown("RIGHT")) return;
        AppLogger.Log("Broadcasting SWITCH_DESKTOP_RIGHT to all peers.");
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = "SWITCH_DESKTOP_RIGHT" });
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
            AppLogger.Log($"Task View requested for {machineName} — sending TASK_VIEW message.");
            _ = _networkManager?.SendToPeerAsync(machineName, new NetworkMessage { Type = "TASK_VIEW" });
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
                Type           = "SWITCH_TO_DESKTOP_INDEX",
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
        info.DesktopNames        = msg.DesktopNames        ?? new();
        info.WallpaperThumbnails = msg.WallpaperThumbnails ?? new();
    }

    private MachineInfo GetOrAdd(string machineName)
    {
        var info = Connections.FirstOrDefault(m =>
            m.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase));
        if (info == null)
        {
            info = new MachineInfo { MachineName = machineName };
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
                Type                = "STATE_UPDATE",
                MachineName         = _networkManager.LocalMachineName,
                CurrentDesktop      = VirtualDesktopHelper.GetCurrentDesktopIndex(),
                TotalDesktops       = VirtualDesktopHelper.GetTotalDesktopCount(),
                IsRdpServer         = _isRdpServer,
                RdpPeers            = _networkManager.ConnectedPeers.ToList(),
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
