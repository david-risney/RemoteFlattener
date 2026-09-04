using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class MachineNameTests
{
    [Theory]
    [InlineData("davris-0.guest.corp.microsoft.com", "DAVRIS-0")]
    [InlineData("machine.domain.com", "MACHINE")]
    [InlineData("DAVRIS-0", "DAVRIS-0")]
    [InlineData("davris-0", "DAVRIS-0")]
    [InlineData("a.b.c.d.e", "A")]
    public void Canonical_StripsToFirstLabelAndUppercases(string input, string expected)
    {
        Assert.Equal(expected, MachineName.From(input).Canonical);
    }

    [Fact]
    public void Value_PreservesObservedFqdn()
    {
        var name = MachineName.From("davris-0.guest.corp.microsoft.com");

        Assert.Equal("davris-0.guest.corp.microsoft.com", name.Value);
    }

    [Fact]
    public void Equality_MatchesFqdnAndShortName()
    {
        Assert.Equal(
            MachineName.From("davris-0.guest.corp.microsoft.com"),
            MachineName.From("DAVRIS-0"));
    }

    [Fact]
    public void Equality_CurrentlyTreatsSameFirstLabelInDifferentDomainsAsSameMachine()
    {
        Assert.Equal(
            MachineName.From("server.domain-one.test"),
            MachineName.From("server.domain-two.test"));
    }

    [Fact]
    public void ObservedValueComparison_DistinguishesFqdnFromShortName()
    {
        Assert.False(
            MachineName.From("server.domain.test").HasSameObservedValue("SERVER"));
    }

    [Fact]
    public void CanonicalToObservedComparison_CurrentlyDoesNotCanonicalizeOtherValue()
    {
        Assert.False(
            MachineName.From("server.domain-one.test")
                .CanonicalEqualsObservedValue("server.domain-two.test"));
    }

    [Fact]
    public void EmptyAndNull_HaveEmptyCanonicalName()
    {
        Assert.Equal(string.Empty, MachineName.From(string.Empty).Canonical);
        Assert.Equal(string.Empty, MachineName.From(null).Canonical);
    }

    [Fact]
    public void Whitespace_IsNotTrimmedDuringCanonicalization()
    {
        Assert.Equal("  SERVER  ", MachineName.From("  server  ").Canonical);
    }
}
