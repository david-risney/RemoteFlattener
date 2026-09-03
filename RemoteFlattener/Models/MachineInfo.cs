using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemoteFlattener.Models;

public class MachineInfo : INotifyPropertyChanged
{
    private string _machineName = string.Empty;
    private int _currentDesktop;
    private int _totalDesktops;
    private bool _isRdpServer;
    private bool _isConnected;
    private bool _isIndirect;
    private List<string> _rdpPeers = new();
    private List<string> _desktopNames = new();

    public string MachineName
    {
        get => _machineName;
        set { _machineName = value.Trim(); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public int CurrentDesktop
    {
        get => _currentDesktop;
        set { _currentDesktop = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public int TotalDesktops
    {
        get => _totalDesktops;
        set { _totalDesktops = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public bool IsRdpServer
    {
        get => _isRdpServer;
        set { _isRdpServer = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    /// <summary>
    /// True when this machine is known only via another peer's RdpPeers list —
    /// i.e. reachable through the mesh but not directly TCP-connected to us.
    /// Always false when <see cref="IsConnected"/> is true.
    /// </summary>
    public bool IsIndirect
    {
        get => _isIndirect;
        set { _isIndirect = value; OnPropertyChanged(); }
    }

    public List<string> RdpPeers
    {
        get => _rdpPeers;
        set { _rdpPeers = value; OnPropertyChanged(); }
    }

    private string? _rdpClientName;

    /// <summary>
    /// The <c>CLIENTNAME</c> reported by this machine when it is an RDP server.
    /// Identifies the RDP client machine (e.g. the physical host running msrdc/mstsc).
    /// Only populated for remote-session peers (Cloud DevBox, AVD, etc.).
    /// </summary>
    public string? RdpClientName
    {
        get => _rdpClientName;
        set { _rdpClientName = value; OnPropertyChanged(); }
    }

    private string? _devBoxFriendlyName;

    /// <summary>
    /// The user-assigned friendly name of this Cloud DevBox (e.g. "davris-10").
    /// Read from the DevBox Agent config. Used to match msrdc window titles on the
    /// client, since msrdc shows the friendly name rather than the machine hostname.
    /// Only populated for DevBox peers.
    /// </summary>
    public string? DevBoxFriendlyName
    {
        get => _devBoxFriendlyName;
        set { _devBoxFriendlyName = value; OnPropertyChanged(); }
    }

    private Dictionary<string, int> _rdpHostedServers = new();

    /// <summary>
    /// Only populated for RDP-client peers.  Maps server machine name (normalized) →
    /// local desktop index on which that server's mstsc window lives.
    /// This is broadcast by the client so every mesh node can construct the same tree.
    /// </summary>
    public Dictionary<string, int> RdpHostedServers
    {
        get => _rdpHostedServers;
        set { _rdpHostedServers = value; OnPropertyChanged(); }
    }

    /// <summary>Desktop display names in order, broadcast by the remote machine.</summary>
    public List<string> DesktopNames
    {
        get => _desktopNames;
        set { _desktopNames = value; OnPropertyChanged(); }
    }

    private List<string> _wallpaperThumbnails = new();

    /// <summary>Base64-encoded JPEG thumbnail per desktop, received over the network.</summary>
    public List<string> WallpaperThumbnails
    {
        get => _wallpaperThumbnails;
        set { _wallpaperThumbnails = value; OnPropertyChanged(); }
    }

    private List<string> _wallpaperColors = new();

    /// <summary>Hex RGB colour string per desktop (e.g. "#1E1E2E"), for solid-colour backgrounds. Empty string = use thumbnail image.</summary>
    public List<string> WallpaperColors
    {
        get => _wallpaperColors;
        set { _wallpaperColors = value; OnPropertyChanged(); }
    }

    public string DisplayText =>
        $"{(IsRdpServer ? "[SERVER]" : "[CLIENT]")} {MachineName}" +
        $"{(TotalDesktops > 0 ? $" (Desktop {CurrentDesktop}/{TotalDesktops})" : "")}" +
        $" {(IsConnected ? "✓" : "✗")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
