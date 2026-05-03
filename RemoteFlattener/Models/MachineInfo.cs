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
    private List<string> _rdpPeers = new();
    private List<string> _desktopNames = new();

    public string MachineName
    {
        get => _machineName;
        set { _machineName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
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

    public List<string> RdpPeers
    {
        get => _rdpPeers;
        set { _rdpPeers = value; OnPropertyChanged(); }
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

    public string DisplayText =>
        $"{(IsRdpServer ? "[SERVER]" : "[CLIENT]")} {MachineName}" +
        $"{(TotalDesktops > 0 ? $" (Desktop {CurrentDesktop}/{TotalDesktops})" : "")}" +
        $" {(IsConnected ? "✓" : "✗")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
