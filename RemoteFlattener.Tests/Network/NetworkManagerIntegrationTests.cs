using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemoteFlattener.Models;
using RemoteFlattener.Network;
using Xunit;

namespace RemoteFlattener.Tests.Network;

/// <summary>
/// Integration tests that spin up multiple <see cref="NetworkManager"/> nodes on localhost
/// and verify handshake, messaging, relay, and authentication-rejection scenarios.
/// </summary>
public class NetworkManagerIntegrationTests : IDisposable
{
    private readonly List<NetworkManager> _nodes = new();

    /// <summary>Finds an available TCP port by binding to port 0.</summary>
    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private NetworkManager CreateNode(string name, int port) =>
        CreateNode(name, port, NullNetworkLogger.Instance);

    private NetworkManager CreateNode(string name, int port, INetworkLogger logger)
    {
        var config = new NetworkManagerConfig
        {
            Port = port,
            LocalMachineName = name,
            Logger = logger,
        };
        var node = new NetworkManager(config);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Polls until a condition is true, or fails after the timeout.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting: {because}");
            await Task.Delay(50);
        }
    }

    // ── Two-node handshake ──────────────────────────────────────────────────

    [Fact]
    public async Task TwoNodes_ConnectAndAuthenticate()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        // A will connect to B as an outgoing peer.
        nodeA.Start("shared-secret", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("shared-secret", Array.Empty<(string, string, int)>());

        await WaitForAsync(
            () => nodeA.ConnectedPeers.Any() && nodeB.ConnectedPeers.Any(),
            TimeSpan.FromSeconds(5),
            "Both nodes should see each other as connected peers");

        Assert.Contains("NODE-B", nodeA.ConnectedPeers);
        Assert.Contains("NODE-A", nodeB.ConnectedPeers);
    }

    // ── Bidirectional connection ────────────────────────────────────────────

    [Fact]
    public async Task TwoNodes_BothConnectToEachOther_OnlyOneConnectionSurvives()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        // Both nodes try to connect to each other — only one connection should win.
        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", new[] { ("NODE-A", "127.0.0.1", portA) });

        // Both sides race: one outgoing handshake wins, the other is detected as duplicate.
        // The loser's outgoing loop retries after a 5s delay, so give it extra time.
        await WaitForAsync(
            () => nodeA.ConnectedPeers.Any() && nodeB.ConnectedPeers.Any(),
            TimeSpan.FromSeconds(20),
            "Both nodes should be connected");

        // Each node should see exactly one peer (the duplicate should be dropped).
        Assert.Single(nodeA.ConnectedPeers);
        Assert.Single(nodeB.ConnectedPeers);
    }

    // ── Message sending ────────────────────────────────────────────────────

    [Fact]
    public async Task TwoNodes_BroadcastMessage_IsReceived()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var received = new ConcurrentBag<(string sender, NetworkMessage msg)>();
        nodeB.MessageReceived += (sender, msg) => received.Add((sender, msg));

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        await nodeA.BroadcastAsync(new NetworkMessage
        {
            Type = MessageTypes.StateUpdate,
            CurrentDesktop = 2,
            TotalDesktops = 4,
        });

        await WaitForAsync(() => received.Count > 0, TimeSpan.FromSeconds(5), "message received");
        var (_, msg) = received.First();
        Assert.Equal(MessageTypes.StateUpdate, msg.Type);
        Assert.Equal(2, msg.CurrentDesktop);
        Assert.Equal(4, msg.TotalDesktops);
        Assert.Equal("NODE-A", msg.OriginMachine);
    }

    // ── Targeted send ──────────────────────────────────────────────────────

    [Fact]
    public async Task TwoNodes_SendToPeer_IsReceived()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var received = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => received.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        await nodeA.SendToPeerAsync("NODE-B", new NetworkMessage
        {
            Type = MessageTypes.SwitchRight,
        });

        await WaitForAsync(() => received.Count > 0, TimeSpan.FromSeconds(5), "message received");
        Assert.Equal(MessageTypes.SwitchRight, received.First().Type);
        Assert.Equal("NODE-B", received.First().TargetMachine);
    }

    // ── Three-node relay ───────────────────────────────────────────────────

    [Fact]
    public async Task ThreeNodes_MessageRelayedThroughMiddle()
    {
        // A <-> B <-> C  (A and C are not directly connected)
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);
        var nodeC = CreateNode("NODE-C", portC);

        var receivedByC = new ConcurrentBag<NetworkMessage>();
        nodeC.MessageReceived += (_, msg) => receivedByC.Add(msg);

        // A connects to B; C connects to B. A and C don't know about each other directly.
        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        nodeC.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });

        await WaitForAsync(
            () => nodeB.ConnectedPeers.Count() >= 2,
            TimeSpan.FromSeconds(5),
            "B should have two peers");

        // A broadcasts — B should relay to C.
        await nodeA.BroadcastAsync(new NetworkMessage
        {
            Type = MessageTypes.StateUpdate,
            CurrentDesktop = 1,
            TotalDesktops = 3,
        });

        await WaitForAsync(() => receivedByC.Count > 0, TimeSpan.FromSeconds(5), "C receives relayed message");
        Assert.Equal("NODE-A", receivedByC.First().OriginMachine);
    }

    // ── Three-node targeted send via relay ──────────────────────────────────

    [Fact]
    public async Task ThreeNodes_TargetedSendRelayedToCorrectNode()
    {
        // A <-> B <-> C
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);
        var nodeC = CreateNode("NODE-C", portC);

        var receivedByB = new ConcurrentBag<NetworkMessage>();
        var receivedByC = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => receivedByB.Add(msg);
        nodeC.MessageReceived += (_, msg) => receivedByC.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        nodeC.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });

        await WaitForAsync(
            () => nodeB.ConnectedPeers.Count() >= 2,
            TimeSpan.FromSeconds(5),
            "B should have two peers");

        // A sends directly to C — should be relayed through B.
        await nodeA.SendToPeerAsync("NODE-C", new NetworkMessage
        {
            Type = MessageTypes.SwitchLeft,
        });

        await WaitForAsync(() => receivedByC.Count > 0, TimeSpan.FromSeconds(5), "C receives targeted message");
        Assert.Equal(MessageTypes.SwitchLeft, receivedByC.First().Type);

        // B should NOT have acted on the message (it was targeted at C),
        // but it still relays it — the MessageReceived event fires only for isForMe.
        Assert.Empty(receivedByB);
    }

    // ── Wrong-password rejection ───────────────────────────────────────────

    [Fact]
    public async Task TwoNodes_WrongPassword_DoNotConnect()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        nodeA.Start("correct-password", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("wrong-password", Array.Empty<(string, string, int)>());

        // Give enough time for multiple connection attempts + auth failures.
        await Task.Delay(2000);

        Assert.Empty(nodeA.ConnectedPeers);
        Assert.Empty(nodeB.ConnectedPeers);
    }

    // ── PeerConnected / PeerDisconnected events ────────────────────────────

    [Fact]
    public async Task Events_PeerConnectedAndDisconnected_AreFired()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var connected = new ConcurrentBag<string>();
        var disconnected = new ConcurrentBag<string>();
        nodeA.PeerConnected += name => connected.Add(name);
        nodeA.PeerDisconnected += name => disconnected.Add(name);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => connected.Count > 0, TimeSpan.FromSeconds(5), "PeerConnected fires");
        Assert.Contains("NODE-B", connected);

        // Stop B — A should detect the disconnect.
        nodeB.Stop();

        await WaitForAsync(() => disconnected.Count > 0, TimeSpan.FromSeconds(5), "PeerDisconnected fires");
        Assert.Contains("NODE-B", disconnected);
    }

    // ── Duplicate message dedup ────────────────────────────────────────────

    [Fact]
    public async Task ThreeNodes_FullMesh_NoDuplicateMessages()
    {
        // All three fully connected — a broadcast should arrive exactly once at each peer.
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);
        var nodeC = CreateNode("NODE-C", portC);

        var receivedByB = new ConcurrentBag<NetworkMessage>();
        var receivedByC = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => receivedByB.Add(msg);
        nodeC.MessageReceived += (_, msg) => receivedByC.Add(msg);

        nodeB.Start("pw", new[] { ("NODE-A", "127.0.0.1", portA) });
        nodeC.Start("pw", new[] { ("NODE-A", "127.0.0.1", portA), ("NODE-B", "127.0.0.1", portB) });
        nodeA.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(
            () => nodeA.ConnectedPeers.Count() >= 2 && nodeB.ConnectedPeers.Count() >= 1 && nodeC.ConnectedPeers.Count() >= 1,
            TimeSpan.FromSeconds(5),
            "full mesh established");

        await nodeA.BroadcastAsync(new NetworkMessage
        {
            Type = MessageTypes.StateUpdate,
            CurrentDesktop = 1,
            TotalDesktops = 2,
        });

        // Wait a bit for relay to propagate and settle.
        await Task.Delay(500);

        // Each peer should receive the message exactly once (no duplicates from relay).
        Assert.Single(receivedByB);
        Assert.Single(receivedByC);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            try { node.Stop(); } catch { }
            try { node.Dispose(); } catch { }
        }
    }
}
