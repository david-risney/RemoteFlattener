using RemoteFlattener.Models;
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

    // ── False-positive prevention ────────────────────────────────────────

    [Fact]
    public void MatchMachineName_ShortNameIsSubstringOfLongerName_DoesNotFalsePositive()
    {
        // "PC" normalizes to "PC"; title hostname "MY-PC" normalizes to "MY-PC" — no match.
        var result = RdpWindowLocator.MatchMachineName(
            "MY-PC - Remote Desktop Connection", ["PC", "MY-PC"]);
        Assert.Equal("MY-PC", result);
    }

    [Fact]
    public void MatchMachineName_NameNotAtStart_DoesNotMatch()
    {
        // Prefix before " - " would be "PREFIX MY-SERVER" which normalizes to "PREFIX" — no match.
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

    // ── FQDN window titles ────────────────────────────────────────────────

    [Fact]
    public void MatchMachineName_FqdnWindowTitle_MatchesShortName()
    {
        // mstsc connected via FQDN → title is "davris-0.guest.corp.microsoft.com - Remote Desktop Connection"
        // Candidate name is the short name "DAVRIS-0" (from ConnectedPeers).
        var result = RdpWindowLocator.MatchMachineName(
            "davris-0.guest.corp.microsoft.com - Remote Desktop Connection", ["DAVRIS-0"]);
        Assert.Equal("DAVRIS-0", result);
    }

    [Fact]
    public void MatchMachineName_FqdnWindowTitle_MatchesFqdnCandidate()
    {
        // Candidate is itself a FQDN — both sides normalize to the same short name.
        var result = RdpWindowLocator.MatchMachineName(
            "davris-0.guest.corp.microsoft.com - Remote Desktop Connection",
            ["davris-0.guest.corp.microsoft.com"]);
        Assert.Equal("davris-0.guest.corp.microsoft.com", result);
    }

    [Fact]
    public void MatchMachineName_ShortTitleVsFqdnCandidate_Matches()
    {
        // mstsc used short name in title, but candidate is FQDN — first label matches.
        var result = RdpWindowLocator.MatchMachineName(
            "DAVRIS-0 - Remote Desktop Connection",
            ["davris-0.guest.corp.microsoft.com"]);
        Assert.Equal("davris-0.guest.corp.microsoft.com", result);
    }

    [Fact]
    public void MatchMachineName_NoSeparatorInTitle_ReturnsNull()
    {
        // Titles without " - " should not match anything.
        var result = RdpWindowLocator.MatchMachineName(
            "DAVRIS-0", ["DAVRIS-0"]);
        Assert.Null(result);
    }
}
