using RemoteFlattener.RDP;
using Xunit;

namespace RemoteFlattener.Tests.RDP;

public class RdpWindowLocatorTests
{
    // ── No match ──────────────────────────────────────────────────────────

    [Fact]
    public void MatchMachineName_NoNamesMatch_ReturnsNull()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "COMPUTER - Remote Desktop Connection", ["SERVER-A", "SERVER-B"]);
        Assert.Null(result);
    }

    [Fact]
    public void MatchMachineName_EmptyNameList_ReturnsNull()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "SERVER-A - Remote Desktop Connection", []);
        Assert.Null(result);
    }

    [Fact]
    public void MatchMachineName_EmptyTitle_ReturnsNull()
    {
        var result = RdpWindowLocator.MatchMachineName("", ["SERVER-A"]);
        Assert.Null(result);
    }

    // ── Basic match ───────────────────────────────────────────────────────

    [Fact]
    public void MatchMachineName_ExactMatch_ReturnsName()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "SERVER-A - Remote Desktop Connection", ["SERVER-A"]);
        Assert.Equal("SERVER-A", result);
    }

    // ── Case-insensitivity ────────────────────────────────────────────────

    [Fact]
    public void MatchMachineName_LowercaseTitle_MatchesUppercaseName()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "server-a - remote desktop connection", ["SERVER-A"]);
        Assert.Equal("SERVER-A", result);
    }

    [Fact]
    public void MatchMachineName_UppercaseTitle_MatchesLowercaseName()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "SERVER-A - Remote Desktop Connection", ["server-a"]);
        Assert.Equal("server-a", result);
    }

    // ── Substring matching ────────────────────────────────────────────────

    [Fact]
    public void MatchMachineName_NameIsSubstringOfTitle_Matches()
    {
        // mstsc titles are "MACHINENAME - Remote Desktop Connection"
        var result = RdpWindowLocator.MatchMachineName(
            "MY-SERVER - Remote Desktop Connection", ["MY-SERVER"]);
        Assert.Equal("MY-SERVER", result);
    }

    [Fact]
    public void MatchMachineName_MultipleNamesMatch_ReturnsFirstInList()
    {
        // Both "SERVER-A" and "SERVER" appear in the title.  First in list wins.
        var result = RdpWindowLocator.MatchMachineName(
            "SERVER-A - Remote Desktop Connection", ["SERVER-A", "SERVER"]);
        Assert.Equal("SERVER-A", result);
    }

    [Fact]
    public void MatchMachineName_SecondNameMatches_ReturnsSecond()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "MACHINE-B - Remote Desktop Connection", ["MACHINE-A", "MACHINE-B"]);
        Assert.Equal("MACHINE-B", result);
    }

    // ── Substring false-positive prevention ────────────────────────────────

    [Fact]
    public void MatchMachineName_ShortNameIsSubstringOfLongerName_DoesNotFalsePositive()
    {
        // "PC" must NOT match "MY-PC - Remote Desktop Connection".
        // The title must START WITH the name, so "PC" (which only appears mid-string) is skipped.
        var result = RdpWindowLocator.MatchMachineName(
            "MY-PC - Remote Desktop Connection", ["PC", "MY-PC"]);
        Assert.Equal("MY-PC", result);
    }

    [Fact]
    public void MatchMachineName_NameNotAtStart_DoesNotMatch()
    {
        // Even with the separator, the name must be at the start of the title.
        var result = RdpWindowLocator.MatchMachineName(
            "PREFIX MY-SERVER - Remote Desktop Connection", ["MY-SERVER"]);
        Assert.Null(result);
    }

    [Fact]
    public void MatchMachineName_WithSeparator_Matches()
    {
        var result = RdpWindowLocator.MatchMachineName(
            "MY-SERVER - Remote Desktop Connection", ["MY-SERVER"]);
        Assert.Equal("MY-SERVER", result);
    }
}
