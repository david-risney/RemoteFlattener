using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using RemoteFlattener.RDP;
using Xunit;

namespace RemoteFlattener.Tests.RDP;

// ── Fake TcpConnectionInformation ─────────────────────────────────────────────

/// <summary>
/// Test double for <see cref="TcpConnectionInformation"/> which is an abstract class.
/// </summary>
internal sealed class FakeTcpConn : TcpConnectionInformation
{
    public override IPEndPoint LocalEndPoint  { get; }
    public override IPEndPoint RemoteEndPoint { get; }
    public override TcpState   State          { get; }

    public FakeTcpConn(IPEndPoint local, IPEndPoint remote, TcpState state = TcpState.Established)
    {
        LocalEndPoint  = local;
        RemoteEndPoint = remote;
        State          = state;
    }
}

// ── ExtractRdpAddresses tests ──────────────────────────────────────────────────

public class RdpConnectionDetectorTests
{
    private static readonly IPAddress Ip1 = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Ip2 = IPAddress.Parse("10.0.0.2");
    private static readonly IPAddress LoopV4 = IPAddress.Loopback;          // 127.0.0.1

    private static FakeTcpConn Conn(int local, int remote, IPAddress? remoteAddr = null,
        TcpState state = TcpState.Established) =>
        new(new IPEndPoint(IPAddress.Any, local),
            new IPEndPoint(remoteAddr ?? Ip1, remote),
            state);

    // ── ExtractRdpAddresses ────────────────────────────────────────────────

    [Fact]
    public void ExtractRdpAddresses_OutboundRdp_ReturnsRemoteAddress()
    {
        // RDP client: local port is ephemeral, remote port is 3389
        var result = RdpConnectionDetector.ExtractRdpAddresses([Conn(55000, 3389)]);
        Assert.Contains(Ip1, result);
    }

    [Fact]
    public void ExtractRdpAddresses_InboundRdp_ReturnsRemoteAddress()
    {
        // RDP server: local port is 3389, remote port is ephemeral
        var result = RdpConnectionDetector.ExtractRdpAddresses([Conn(3389, 55000)]);
        Assert.Contains(Ip1, result);
    }

    [Fact]
    public void ExtractRdpAddresses_NonRdpConnection_IsIgnored()
    {
        var result = RdpConnectionDetector.ExtractRdpAddresses([Conn(443, 51000)]);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractRdpAddresses_NonEstablished_IsIgnored()
    {
        var conn = new FakeTcpConn(
            new IPEndPoint(IPAddress.Any, 3389),
            new IPEndPoint(Ip1, 55000),
            TcpState.TimeWait);
        var result = RdpConnectionDetector.ExtractRdpAddresses([conn]);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractRdpAddresses_DuplicateAddress_DeduplicatesIt()
    {
        var result = RdpConnectionDetector.ExtractRdpAddresses([
            Conn(3389, 55000),   // inbound from Ip1
            Conn(55001, 3389),   // outbound to Ip1
        ]);
        Assert.Single(result);
    }

    [Fact]
    public void ExtractRdpAddresses_MultipleDistinctAddresses_ReturnsAll()
    {
        var result = RdpConnectionDetector.ExtractRdpAddresses([
            Conn(3389, 55000, Ip1),
            Conn(3389, 55001, Ip2),
        ]);
        Assert.Contains(Ip1, result);
        Assert.Contains(Ip2, result);
        Assert.Equal(2, System.Linq.Enumerable.Count(result));
    }

    [Fact]
    public void ExtractRdpAddresses_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(RdpConnectionDetector.ExtractRdpAddresses([]));
    }

    // ── ResolveToHostnames ────────────────────────────────────────────────

    [Fact]
    public void ResolveToHostnames_NormalAddress_ResolvesAndNormalizesHostname()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [Ip1], "SELF", _ => "server.corp.com");
        Assert.Contains("SERVER", result);
    }

    [Fact]
    public void ResolveToHostnames_LoopbackAddress_IsSkipped()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [LoopV4], "SELF", _ => "localhost");
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveToHostnames_SelfAddress_IsFiltered()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [Ip1], "MY-PC", _ => "MY-PC.corp.com");
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveToHostnames_SelfFilter_IsCaseInsensitive()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [Ip1], "my-pc", _ => "MY-PC.corp.com");
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveToHostnames_DnsThrows_FallsBackToIpString()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [Ip1], "SELF", _ => throw new System.Net.Sockets.SocketException());
        Assert.Contains("10.0.0.1", result);
    }

    [Fact]
    public void ResolveToHostnames_EmptyInput_ReturnsEmpty()
    {
        var result = RdpConnectionDetector.ResolveToHostnames([], "SELF", _ => "host");
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveToHostnames_FqdnIsShortenedToShortName()
    {
        var result = RdpConnectionDetector.ResolveToHostnames(
            [Ip1], "SELF", _ => "server-01.corp.example.com");
        Assert.Contains("SERVER-01", result);
    }
}
