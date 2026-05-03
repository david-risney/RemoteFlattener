using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using RemoteFlattener.Hotkeys;
using RemoteFlattener.Models;
using RemoteFlattener.Network;
using RemoteFlattener.RDP;
using RemoteFlattener.VirtualDesktop;

namespace RemoteFlattener;

public partial class MainWindow : Window
{
    private NetworkManager? _networkManager;
    private HotkeyManager? _hotkeyManager;
    private TreeWindow? _treeWindow;
    private Timer? _stateTimer;

    private bool _isRunning;
    private bool _isRdpServer;

    /// <summary>Bound to the ConnectionList ListBox.</summary>
    public ObservableCollection<MachineInfo> Connections { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        PasswordBox.Text = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Please enter or generate a password first.",
                "RemoteFlattener", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var machines = (MachinesBox.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _isRdpServer = RdpRoleDetector.IsRemoteSession();
        _isRunning   = true;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled  = true;
        RefreshStatusLabel();

        // Start networking.
        _networkManager = new NetworkManager();
        _networkManager.MessageReceived  += OnMessageReceived;
        _networkManager.PeerConnected    += OnPeerConnected;
        _networkManager.PeerDisconnected += OnPeerDisconnected;
        _networkManager.Start(password, machines);

        // Broadcast our desktop state every 5 seconds so peers stay current.
        _stateTimer = new Timer(_ => BroadcastOurState(), null, 1_000, 5_000);

        // Install keyboard hook only when acting as RDP server.
        if (_isRdpServer)
        {
            _hotkeyManager = new HotkeyManager();
            _hotkeyManager.WinTabPressed      += OnWinTabPressed;
            _hotkeyManager.SwitchDesktopLeft  += OnSendSwitchLeft;
            _hotkeyManager.SwitchDesktopRight += OnSendSwitchRight;
            _hotkeyManager.Install();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopAll();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled  = false;
        RefreshStatusLabel();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void StopAll()
    {
        _isRunning = false;

        _stateTimer?.Dispose();
        _stateTimer = null;

        _hotkeyManager?.Uninstall();
        _hotkeyManager = null;

        _networkManager?.Stop();
        _networkManager?.Dispose();
        _networkManager = null;

        _treeWindow?.Close();
        _treeWindow = null;

        Connections.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAll();
        base.OnClosed(e);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void RefreshStatusLabel()
    {
        StatusLabel.Content = _isRunning
            ? (_isRdpServer ? "Running – RDP Server" : "Running – RDP Client")
            : "Stopped";
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
                    VirtualDesktopSwitcher.SwitchLeft();
                    break;

                case "SWITCH_DESKTOP_RIGHT" when !_isRdpServer:
                    VirtualDesktopSwitcher.SwitchRight();
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
        Dispatcher.Invoke(() =>
        {
            if (_treeWindow is { IsVisible: true })
            {
                _treeWindow.Close();
                _treeWindow = null;
            }
            else
            {
                _treeWindow = new TreeWindow(
                    Connections,
                    _networkManager?.LocalMachineName ?? Environment.MachineName,
                    _isRdpServer);
                _treeWindow.Closed += (_, _) => _treeWindow = null;
                _treeWindow.Show();
            }
        });
    }

    private void OnSendSwitchLeft()  =>
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = "SWITCH_DESKTOP_LEFT" });

    private void OnSendSwitchRight() =>
        _networkManager?.BroadcastAsync(new NetworkMessage { Type = "SWITCH_DESKTOP_RIGHT" });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpsertMachineInfo(string machineName, NetworkMessage msg)
    {
        var info = GetOrAdd(machineName);
        info.CurrentDesktop = msg.CurrentDesktop;
        info.TotalDesktops  = msg.TotalDesktops;
        info.IsRdpServer    = msg.IsRdpServer;
        info.IsConnected    = true;
        info.RdpPeers       = msg.RdpPeers ?? new();
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

        var msg = new NetworkMessage
        {
            Type           = "STATE_UPDATE",
            MachineName    = _networkManager.LocalMachineName,
            CurrentDesktop = VirtualDesktopHelper.GetCurrentDesktopIndex(),
            TotalDesktops  = VirtualDesktopHelper.GetTotalDesktopCount(),
            IsRdpServer    = _isRdpServer,
            RdpPeers       = _networkManager.ConnectedPeers.ToList()
        };
        _ = _networkManager.BroadcastAsync(msg);
    }
}
