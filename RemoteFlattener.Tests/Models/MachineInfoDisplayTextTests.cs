using System.Collections.Generic;
using System.ComponentModel;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class MachineInfoDisplayTextTests
{
    // ── Role prefix ───────────────────────────────────────────────────────

    [Fact]
    public void DisplayText_WhenIsRdpServer_StartsWithServerTag()
    {
        var m = new MachineInfo { MachineName = "PC", IsRdpServer = true };
        Assert.StartsWith("[SERVER]", m.DisplayText);
    }

    [Fact]
    public void DisplayText_WhenNotIsRdpServer_StartsWithClientTag()
    {
        var m = new MachineInfo { MachineName = "PC", IsRdpServer = false };
        Assert.StartsWith("[CLIENT]", m.DisplayText);
    }

    // ── Machine name ──────────────────────────────────────────────────────

    [Fact]
    public void DisplayText_ContainsMachineName()
    {
        var m = new MachineInfo { MachineName = "MY-PC" };
        Assert.Contains("MY-PC", m.DisplayText);
    }

    // ── Desktop info ──────────────────────────────────────────────────────

    [Fact]
    public void DisplayText_WhenTotalDesktopsIsZero_OmitsDesktopInfo()
    {
        var m = new MachineInfo { MachineName = "PC", TotalDesktops = 0, CurrentDesktop = 0 };
        Assert.DoesNotContain("Desktop", m.DisplayText);
    }

    [Fact]
    public void DisplayText_WhenTotalDesktopsIsPositive_IncludesDesktopInfo()
    {
        var m = new MachineInfo { MachineName = "PC", TotalDesktops = 4, CurrentDesktop = 2 };
        Assert.Contains("(Desktop 2/4)", m.DisplayText);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(10, 10)]
    public void DisplayText_DesktopNumbers_AreCorrect(int current, int total)
    {
        var m = new MachineInfo { MachineName = "PC", TotalDesktops = total, CurrentDesktop = current };
        Assert.Contains($"(Desktop {current}/{total})", m.DisplayText);
    }

    // ── Connection indicator ──────────────────────────────────────────────

    [Fact]
    public void DisplayText_WhenConnected_ShowsCheckmark()
    {
        var m = new MachineInfo { MachineName = "PC", IsConnected = true };
        Assert.Contains("✓", m.DisplayText);
        Assert.DoesNotContain("✗", m.DisplayText);
    }

    [Fact]
    public void DisplayText_WhenNotConnected_ShowsCross()
    {
        var m = new MachineInfo { MachineName = "PC", IsConnected = false };
        Assert.Contains("✗", m.DisplayText);
        Assert.DoesNotContain("✓", m.DisplayText);
    }

    // ── PropertyChanged notifications ─────────────────────────────────────

    private static List<string> CollectChanges(MachineInfo m, System.Action act)
    {
        var names = new List<string>();
        m.PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        act();
        return names;
    }

    [Fact]
    public void IsRdpServer_Setter_RaisesDisplayTextChanged()
    {
        var m = new MachineInfo { MachineName = "PC" };
        var names = CollectChanges(m, () => m.IsRdpServer = true);
        Assert.Contains(nameof(MachineInfo.DisplayText), names);
    }

    [Fact]
    public void IsConnected_Setter_RaisesDisplayTextChanged()
    {
        var m = new MachineInfo { MachineName = "PC" };
        var names = CollectChanges(m, () => m.IsConnected = true);
        Assert.Contains(nameof(MachineInfo.DisplayText), names);
    }

    [Fact]
    public void CurrentDesktop_Setter_RaisesDisplayTextChanged()
    {
        var m = new MachineInfo { MachineName = "PC", TotalDesktops = 4 };
        var names = CollectChanges(m, () => m.CurrentDesktop = 2);
        Assert.Contains(nameof(MachineInfo.DisplayText), names);
    }

    [Fact]
    public void TotalDesktops_Setter_RaisesDisplayTextChanged()
    {
        var m = new MachineInfo { MachineName = "PC" };
        var names = CollectChanges(m, () => m.TotalDesktops = 4);
        Assert.Contains(nameof(MachineInfo.DisplayText), names);
    }

    [Fact]
    public void MachineName_Setter_RaisesDisplayTextChanged()
    {
        var m = new MachineInfo { MachineName = "PC" };
        var names = CollectChanges(m, () => m.MachineName = "NEW-PC");
        Assert.Contains(nameof(MachineInfo.DisplayText), names);
    }
}
