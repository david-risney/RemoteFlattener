using System;
using System.Linq;
using RemoteFlattener.VirtualDesktop;
using Xunit;

namespace RemoteFlattener.Tests.VirtualDesktop;

public class VirtualDesktopHelperTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    /// Packs one or more GUIDs into the flat 16-byte-per-entry byte array
    /// that the Windows registry stores in VirtualDesktopIDs.
    private static byte[] Pack(params Guid[] guids) =>
        guids.SelectMany(g => g.ToByteArray()).ToArray();

    // ── FindGuidIndex — basic cases ────────────────────────────────────────

    [Fact]
    public void FindGuidIndex_SingleEntry_MatchingGuid_ReturnsOne()
    {
        var g = Guid.NewGuid();
        Assert.Equal(1, VirtualDesktopHelper.FindGuidIndex(g, Pack(g)));
    }

    [Fact]
    public void FindGuidIndex_GuidNotPresent_ReturnsZero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(0, VirtualDesktopHelper.FindGuidIndex(b, Pack(a)));
    }

    [Fact]
    public void FindGuidIndex_EmptyArray_ReturnsZero()
    {
        Assert.Equal(0, VirtualDesktopHelper.FindGuidIndex(Guid.NewGuid(), Array.Empty<byte>()));
    }

    // ── FindGuidIndex — multi-desktop ──────────────────────────────────────

    [Fact]
    public void FindGuidIndex_FirstOfThree_ReturnsOne()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(1, VirtualDesktopHelper.FindGuidIndex(a, Pack(a, b, c)));
    }

    [Fact]
    public void FindGuidIndex_MiddleOfThree_ReturnsTwo()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(2, VirtualDesktopHelper.FindGuidIndex(b, Pack(a, b, c)));
    }

    [Fact]
    public void FindGuidIndex_LastOfThree_ReturnsThree()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(3, VirtualDesktopHelper.FindGuidIndex(c, Pack(a, b, c)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    public void FindGuidIndex_LastOfN_ReturnsN(int n)
    {
        var guids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(n, VirtualDesktopHelper.FindGuidIndex(guids[^1], Pack(guids)));
    }

    // ── FindGuidIndex — edge / boundary cases ──────────────────────────────

    [Fact]
    public void FindGuidIndex_PartialTrailingBytes_IgnoresTail()
    {
        // 16 valid bytes + 5 extra — the tail should not cause an exception or wrong result.
        var g   = Guid.NewGuid();
        var buf = Pack(g).Concat(new byte[5]).ToArray(); // 21 bytes total
        Assert.Equal(1, VirtualDesktopHelper.FindGuidIndex(g, buf));
    }

    [Fact]
    public void FindGuidIndex_GuidEmpty_MatchesPackedEmpty()
    {
        // Guid.Empty packed is 16 zero bytes; verify the method handles it correctly.
        Assert.Equal(1, VirtualDesktopHelper.FindGuidIndex(Guid.Empty, Pack(Guid.Empty)));
    }

    [Fact]
    public void FindGuidIndex_GuidEmpty_NotInList_ReturnsZero()
    {
        var g = Guid.NewGuid();
        Assert.Equal(0, VirtualDesktopHelper.FindGuidIndex(Guid.Empty, Pack(g)));
    }

    [Fact]
    public void FindGuidIndex_NoDuplicateMatch_ReturnFirstOccurrence()
    {
        // If somehow the same GUID appears twice, we want the first (lowest) index.
        var g = Guid.NewGuid();
        Assert.Equal(1, VirtualDesktopHelper.FindGuidIndex(g, Pack(g, g)));
    }
}
