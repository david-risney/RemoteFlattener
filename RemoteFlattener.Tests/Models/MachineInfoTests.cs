using System.Collections.Generic;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class MachineInfoTests
{
    // ── NormalizeHostname ──────────────────────────────────────────────────

    [Theory]
    [InlineData("davris-0.guest.corp.microsoft.com", "DAVRIS-0")]
    [InlineData("machine.domain.com",               "MACHINE")]
    [InlineData("DAVRIS-0",                          "DAVRIS-0")]
    [InlineData("davris-0",                          "DAVRIS-0")]
    [InlineData("a.b.c.d.e",                         "A")]
    public void NormalizeHostname_StripsToFirstLabel_Uppercased(string input, string expected)
    {
        Assert.Equal(expected, MachineInfo.NormalizeHostname(input));
    }

    [Fact]
    public void NormalizeHostname_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MachineInfo.NormalizeHostname(string.Empty));
    }

    [Fact]
    public void NormalizeHostname_NoSeparator_UppercasesEntireName()
    {
        Assert.Equal("MYPC", MachineInfo.NormalizeHostname("mypc"));
    }

    // ── MachineName setter preserves value ─────────────────────────────────

    [Fact]
    public void MachineName_Setter_PreservesFqdn()
    {
        var info = new MachineInfo { MachineName = "davris-0.corp.com" };
        Assert.Equal("davris-0.corp.com", info.MachineName);
    }

    [Fact]
    public void MachineName_Setter_TrimsWhitespace()
    {
        var info = new MachineInfo { MachineName = "  MYPC  " };
        Assert.Equal("MYPC", info.MachineName);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────

    [Fact]
    public void MachineName_Changed_FiresPropertyChanged()
    {
        var info   = new MachineInfo();
        var events = new List<string?>();
        info.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        info.MachineName = "TESTPC";

        Assert.Contains(nameof(MachineInfo.MachineName), events);
    }

    [Fact]
    public void IsConnected_Changed_FiresPropertyChanged()
    {
        var info   = new MachineInfo();
        var events = new List<string?>();
        info.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        info.IsConnected = true;

        Assert.Contains(nameof(MachineInfo.IsConnected), events);
    }

    [Fact]
    public void IsIndirect_Changed_FiresPropertyChanged()
    {
        var info   = new MachineInfo();
        var events = new List<string?>();
        info.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        info.IsIndirect = true;

        Assert.Contains(nameof(MachineInfo.IsIndirect), events);
    }

    [Fact]
    public void CurrentDesktop_Changed_FiresPropertyChanged()
    {
        var info   = new MachineInfo();
        var events = new List<string?>();
        info.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        info.CurrentDesktop = 3;

        Assert.Contains(nameof(MachineInfo.CurrentDesktop), events);
    }

    [Fact]
    public void RdpClientName_Changed_FiresPropertyChanged()
    {
        var info   = new MachineInfo();
        var events = new List<string?>();
        info.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        info.RdpClientName = "DAVRIS-10";

        Assert.Contains(nameof(MachineInfo.RdpClientName), events);
    }

    // ── Collection defaults ───────────────────────────────────────────────

    [Fact]
    public void RdpPeers_Default_IsEmptyList()
    {
        var info = new MachineInfo();
        Assert.NotNull(info.RdpPeers);
        Assert.Empty(info.RdpPeers);
    }

    [Fact]
    public void RdpClientName_Default_IsNull()
    {
        var info = new MachineInfo();
        Assert.Null(info.RdpClientName);
    }

    [Fact]
    public void RdpHostedServers_Default_IsEmptyDictionary()
    {
        var info = new MachineInfo();
        Assert.NotNull(info.RdpHostedServers);
        Assert.Empty(info.RdpHostedServers);
    }

    [Fact]
    public void DesktopNames_Default_IsEmptyList()
    {
        var info = new MachineInfo();
        Assert.NotNull(info.DesktopNames);
        Assert.Empty(info.DesktopNames);
    }

    [Fact]
    public void WallpaperThumbnails_Default_IsEmptyList()
    {
        var info = new MachineInfo();
        Assert.NotNull(info.WallpaperThumbnails);
        Assert.Empty(info.WallpaperThumbnails);
    }
}
