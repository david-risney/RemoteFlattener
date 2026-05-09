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

    // ═══════════════════════════════════════════════════════════════════════
    // Reconnection & resilience
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reconnect_PeerRestartsOnSamePort_ReconnectsAutomatically()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "initial connect");

        // Stop B — A should detect disconnect.
        nodeB.Stop();
        nodeB.Dispose();
        _nodes.Remove(nodeB);
        await WaitForAsync(() => !nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "A detects disconnect");

        // Restart B on the same port — A's outgoing loop should reconnect.
        var nodeB2 = CreateNode("NODE-B", portB);
        nodeB2.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(
            () => nodeA.ConnectedPeers.Contains("NODE-B"),
            TimeSpan.FromSeconds(10),
            "A reconnects to restarted B");
    }

    [Fact]
    public async Task Reconnect_MessagesFlowAfterReconnect()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "initial connect");

        // Kill B and restart.
        nodeB.Stop();
        nodeB.Dispose();
        _nodes.Remove(nodeB);

        var nodeB2 = CreateNode("NODE-B", portB);
        var received = new ConcurrentBag<NetworkMessage>();
        nodeB2.MessageReceived += (_, msg) => received.Add(msg);
        nodeB2.Start("pw", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => nodeA.ConnectedPeers.Contains("NODE-B"), TimeSpan.FromSeconds(10), "reconnected");

        await nodeA.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate, CurrentDesktop = 5 });
        await WaitForAsync(() => received.Count > 0, TimeSpan.FromSeconds(5), "message after reconnect");
        Assert.Equal(5, received.First().CurrentDesktop);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Topology & scale
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FourNodeChain_MessageRelayedEndToEnd()
    {
        // A <-> B <-> C <-> D
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort(), portD = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);
        var nodeC = CreateNode("NODE-C", portC);
        var nodeD = CreateNode("NODE-D", portD);

        var receivedByD = new ConcurrentBag<NetworkMessage>();
        nodeD.MessageReceived += (_, msg) => receivedByD.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        nodeC.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeD.Start("pw", new[] { ("NODE-C", "127.0.0.1", portC) });

        await WaitForAsync(
            () => nodeB.ConnectedPeers.Count() >= 2 && nodeC.ConnectedPeers.Count() >= 2,
            TimeSpan.FromSeconds(5),
            "chain established");

        await nodeA.BroadcastAsync(new NetworkMessage
        {
            Type = MessageTypes.StateUpdate,
            CurrentDesktop = 7,
            TotalDesktops = 10,
        });

        await WaitForAsync(() => receivedByD.Count > 0, TimeSpan.FromSeconds(5), "D receives relayed message from A");
        Assert.Equal("NODE-A", receivedByD.First().OriginMachine);
        Assert.Equal(7, receivedByD.First().CurrentDesktop);
    }

    [Fact]
    public async Task StarTopology_HubRelaysToAllSpokes()
    {
        // Hub <-> Spoke1, Hub <-> Spoke2, Hub <-> Spoke3
        int portHub = GetFreePort();
        int portS1 = GetFreePort(), portS2 = GetFreePort(), portS3 = GetFreePort();
        var hub = CreateNode("HUB", portHub);
        var spoke1 = CreateNode("SPOKE-1", portS1);
        var spoke2 = CreateNode("SPOKE-2", portS2);
        var spoke3 = CreateNode("SPOKE-3", portS3);

        var recv1 = new ConcurrentBag<NetworkMessage>();
        var recv2 = new ConcurrentBag<NetworkMessage>();
        var recv3 = new ConcurrentBag<NetworkMessage>();
        spoke1.MessageReceived += (_, msg) => recv1.Add(msg);
        spoke2.MessageReceived += (_, msg) => recv2.Add(msg);
        spoke3.MessageReceived += (_, msg) => recv3.Add(msg);

        hub.Start("pw", Array.Empty<(string, string, int)>());
        spoke1.Start("pw", new[] { ("HUB", "127.0.0.1", portHub) });
        spoke2.Start("pw", new[] { ("HUB", "127.0.0.1", portHub) });
        spoke3.Start("pw", new[] { ("HUB", "127.0.0.1", portHub) });

        await WaitForAsync(() => hub.ConnectedPeers.Count() >= 3, TimeSpan.FromSeconds(5), "all spokes connected");

        // Spoke1 broadcasts — hub relays to spoke2 and spoke3.
        await spoke1.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate, CurrentDesktop = 1 });
        await WaitForAsync(
            () => recv2.Count > 0 && recv3.Count > 0,
            TimeSpan.FromSeconds(5),
            "spoke2 and spoke3 receive via hub");

        Assert.Equal("SPOKE-1", recv2.First().OriginMachine);
        Assert.Equal("SPOKE-1", recv3.First().OriginMachine);
    }

    [Fact]
    public async Task LateJoiner_ReceivesMessagesAfterJoining()
    {
        // A and B already connected. C joins later and should receive new messages.
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        nodeA.Start("pw", Array.Empty<(string, string, int)>());
        nodeB.Start("pw", new[] { ("NODE-A", "127.0.0.1", portA) });
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "A-B connected");

        // C joins late.
        var nodeC = CreateNode("NODE-C", portC);
        var receivedByC = new ConcurrentBag<NetworkMessage>();
        nodeC.MessageReceived += (_, msg) => receivedByC.Add(msg);
        nodeC.Start("pw", new[] { ("NODE-A", "127.0.0.1", portA) });
        await WaitForAsync(() => nodeC.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "C connected to A");

        // B broadcasts — A relays to C.
        await nodeB.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate, CurrentDesktop = 3 });
        await WaitForAsync(() => receivedByC.Count > 0, TimeSpan.FromSeconds(5), "C receives message");
        Assert.Equal("NODE-B", receivedByC.First().OriginMachine);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Protocol edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProtocolVersionMismatch_ConnectionRejected()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        // Manually connect and send a HELLO with wrong protocol version.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        var stream = client.GetStream();
        var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
        var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);

        // Build a valid HELLO but with wrong protocol version.
        var nm = new NetworkManager("pw");
        var hello = new NetworkMessage
        {
            Type = MessageTypes.Hello,
            MachineName = "FAKE-NODE",
            Hmac = nm.ComputeHmac("FAKE-NODE"),
            ProtocolVersion = 999,
        };
        await writer.WriteAsync(hello.Serialize());
        await writer.FlushAsync();

        // Server should close the connection without sending HELLO_ACK.
        await Task.Delay(500);
        Assert.Empty(nodeB.ConnectedPeers);
    }

    [Fact]
    public async Task BroadcastAsync_ZeroPeers_CompletesWithoutError()
    {
        int portA = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        nodeA.Start("pw", Array.Empty<(string, string, int)>());

        // Should not throw.
        await nodeA.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate });
    }

    [Fact]
    public async Task SendToPeer_UnknownPeer_FloodsAllConnections()
    {
        // A connected to B. A sends targeted message to "NODE-X" (not connected).
        // Should flood to B (relay path).
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var receivedByB = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => receivedByB.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        await nodeA.SendToPeerAsync("NODE-X", new NetworkMessage { Type = MessageTypes.SwitchLeft });

        // B should NOT fire MessageReceived for this (targeted at NODE-X, not NODE-B),
        // but B still receives it on the wire for relay purposes.
        await Task.Delay(500);
        // The message is targeted at NODE-X so B's MessageReceived won't fire (isForMe = false).
        Assert.Empty(receivedByB);
    }

    [Fact]
    public async Task RapidBroadcasts_AllMessagesReceived_NoInterleaving()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var received = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => received.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        const int count = 50;
        var tasks = Enumerable.Range(0, count).Select(i =>
            nodeA.BroadcastAsync(new NetworkMessage
            {
                Type = MessageTypes.StateUpdate,
                CurrentDesktop = i,
            })).ToArray();
        await Task.WhenAll(tasks);

        await WaitForAsync(() => received.Count >= count, TimeSpan.FromSeconds(10),
            $"all {count} messages received (got {received.Count})");

        // Verify all desktop indices arrived (order may vary due to concurrency).
        var desktops = received.Select(m => m.CurrentDesktop).OrderBy(d => d).ToList();
        Assert.Equal(Enumerable.Range(0, count), desktops);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bidirectional messaging
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoNodes_SimultaneousSends_BothReceive()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var receivedByA = new ConcurrentBag<NetworkMessage>();
        var receivedByB = new ConcurrentBag<NetworkMessage>();
        nodeA.MessageReceived += (_, msg) => receivedByA.Add(msg);
        nodeB.MessageReceived += (_, msg) => receivedByB.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any() && nodeB.ConnectedPeers.Any(),
            TimeSpan.FromSeconds(5), "connected");

        // Both send at the same time.
        var sendA = nodeA.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate, CurrentDesktop = 1 });
        var sendB = nodeB.BroadcastAsync(new NetworkMessage { Type = MessageTypes.StateUpdate, CurrentDesktop = 2 });
        await Task.WhenAll(sendA, sendB);

        await WaitForAsync(() => receivedByA.Count > 0 && receivedByB.Count > 0,
            TimeSpan.FromSeconds(5), "both receive");

        Assert.Equal(2, receivedByA.First().CurrentDesktop); // A got B's message
        Assert.Equal(1, receivedByB.First().CurrentDesktop); // B got A's message
    }

    [Fact]
    public async Task ReplyPattern_BReceivesAndResponds()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var receivedByA = new ConcurrentBag<NetworkMessage>();
        nodeA.MessageReceived += (_, msg) => receivedByA.Add(msg);

        // B auto-replies when it gets a SwitchRight.
        nodeB.MessageReceived += async (_, msg) =>
        {
            if (msg.Type == MessageTypes.SwitchRight)
            {
                await nodeB.BroadcastAsync(new NetworkMessage
                {
                    Type = MessageTypes.StateUpdate,
                    CurrentDesktop = 99,
                });
            }
        };

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        await nodeA.BroadcastAsync(new NetworkMessage { Type = MessageTypes.SwitchRight });

        await WaitForAsync(() => receivedByA.Count > 0, TimeSpan.FromSeconds(5), "A receives reply");
        Assert.Equal(MessageTypes.StateUpdate, receivedByA.First().Type);
        Assert.Equal(99, receivedByA.First().CurrentDesktop);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Stop / Dispose lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StopThenStart_WorksCorrectly()
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "first connect");

        // Stop both, then restart with fresh passwords.
        nodeA.Stop();
        nodeB.Stop();
        await Task.Delay(200);

        // Re-create B on a new port since we can't rebind easily.
        _nodes.Remove(nodeB);
        int portB2 = GetFreePort();
        var nodeB2 = CreateNode("NODE-B", portB2);
        nodeA.Start("pw2", new[] { ("NODE-B", "127.0.0.1", portB2) });
        nodeB2.Start("pw2", Array.Empty<(string, string, int)>());

        await WaitForAsync(() => nodeA.ConnectedPeers.Contains("NODE-B"), TimeSpan.FromSeconds(5), "reconnected after restart");
    }

    [Fact]
    public void Dispose_CleansUpConnections_NoLeakedListeners()
    {
        int port = GetFreePort();
        var node = CreateNode("NODE-X", port);
        node.Start("pw", Array.Empty<(string, string, int)>());

        // Dispose should not throw and should release the port.
        node.Dispose();
        _nodes.Remove(node);

        // Verify port is free by binding to it again.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        try
        {
            listener.Start();
            // Success — port was released.
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void DoubleStop_DoesNotThrow()
    {
        int port = GetFreePort();
        var node = CreateNode("NODE-X", port);
        node.Start("pw", Array.Empty<(string, string, int)>());

        node.Stop();
        node.Stop(); // Should not throw.
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        int port = GetFreePort();
        var node = CreateNode("NODE-X", port);
        node.Start("pw", Array.Empty<(string, string, int)>());

        node.Dispose();
        node.Dispose(); // Should not throw.
        _nodes.Remove(node);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Malformed / hostile input
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MalformedInput_GarbageBytes_NodeSurvives()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        // Connect and send random garbage.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        var stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("not json at all\nmore garbage\n"));
        await stream.FlushAsync();
        client.Close();

        await Task.Delay(500);

        // Node should still be alive and accepting connections.
        int portA = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5),
            "B still accepts connections after garbage input");
    }

    [Fact]
    public async Task MalformedInput_WrongMessageTypeDuringHandshake_Rejected()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        // Send a valid JSON message but with wrong type (STATE_UPDATE instead of HELLO).
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        var stream = client.GetStream();
        var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
        var badMsg = new NetworkMessage { Type = MessageTypes.StateUpdate, MachineName = "FAKE" };
        await writer.WriteAsync(badMsg.Serialize());
        await writer.FlushAsync();

        await Task.Delay(500);
        Assert.Empty(nodeB.ConnectedPeers);
    }

    [Fact]
    public async Task MalformedInput_ImmediateDisconnect_NodeSurvives()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        // Connect then immediately close — no data sent.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        client.Close();

        await Task.Delay(500);

        // Node should still work.
        int portA = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5),
            "B still accepts connections after immediate disconnect");
    }

    [Fact]
    public async Task MalformedInput_HelloWithEmptyHmac_Rejected()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        var stream = client.GetStream();
        var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);

        var hello = new NetworkMessage
        {
            Type = MessageTypes.Hello,
            MachineName = "ATTACKER",
            Hmac = "",
            ProtocolVersion = NetworkManager.ProtocolVersion,
        };
        await writer.WriteAsync(hello.Serialize());
        await writer.FlushAsync();

        await Task.Delay(500);
        Assert.Empty(nodeB.ConnectedPeers);
    }

    [Fact]
    public async Task MalformedInput_HelloWithMissingMachineName_Rejected()
    {
        int portB = GetFreePort();
        var nodeB = CreateNode("NODE-B", portB);
        nodeB.Start("pw", Array.Empty<(string, string, int)>());

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", portB);
        var stream = client.GetStream();
        var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);

        var nm = new NetworkManager("pw");
        var hello = new NetworkMessage
        {
            Type = MessageTypes.Hello,
            MachineName = null,
            Hmac = nm.ComputeHmac(""),
            ProtocolVersion = NetworkManager.ProtocolVersion,
        };
        await writer.WriteAsync(hello.Serialize());
        await writer.FlushAsync();

        await Task.Delay(500);
        Assert.Empty(nodeB.ConnectedPeers);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Field preservation through relay
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Relay_AllNetworkMessageFields_PreservedThroughRelay()
    {
        // A <-> B <-> C — verify all fields survive the relay hop.
        int portA = GetFreePort(), portB = GetFreePort(), portC = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);
        var nodeC = CreateNode("NODE-C", portC);

        var receivedByC = new ConcurrentBag<NetworkMessage>();
        nodeC.MessageReceived += (_, msg) => receivedByC.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        nodeC.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });

        await WaitForAsync(() => nodeB.ConnectedPeers.Count() >= 2, TimeSpan.FromSeconds(5), "chain established");

        await nodeA.BroadcastAsync(new NetworkMessage
        {
            Type = MessageTypes.StateUpdate,
            CurrentDesktop = 3,
            TotalDesktops = 5,
            IsRdpServer = true,
            RdpPeers = new List<string> { "SERVER-1", "SERVER-2" },
            DesktopNames = new List<string> { "Work", "Personal", "Gaming", "Media", "Dev" },
            WallpaperThumbnails = new List<string> { "base64img1", "", "base64img3" },
            RdpHostedServers = new Dictionary<string, int> { ["SERVER-1"] = 0, ["SERVER-2"] = 2 },
        });

        await WaitForAsync(() => receivedByC.Count > 0, TimeSpan.FromSeconds(5), "C receives relayed message");
        var msg = receivedByC.First();

        Assert.Equal(MessageTypes.StateUpdate, msg.Type);
        Assert.Equal("NODE-A", msg.OriginMachine);
        Assert.Equal(3, msg.CurrentDesktop);
        Assert.Equal(5, msg.TotalDesktops);
        Assert.True(msg.IsRdpServer);
        Assert.Equal(new[] { "SERVER-1", "SERVER-2" }, msg.RdpPeers);
        Assert.Equal(new[] { "Work", "Personal", "Gaming", "Media", "Dev" }, msg.DesktopNames);
        Assert.Equal(new[] { "base64img1", "", "base64img3" }, msg.WallpaperThumbnails);
        Assert.Equal(2, msg.RdpHostedServers!.Count);
        Assert.Equal(0, msg.RdpHostedServers["SERVER-1"]);
        Assert.Equal(2, msg.RdpHostedServers["SERVER-2"]);
    }

    [Fact]
    public async Task Relay_OriginMachine_NotOverwrittenByRelayNode()
    {
        // A <-> B <-> C — OriginMachine should stay "NODE-A" even after B relays.
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
        await WaitForAsync(() => nodeB.ConnectedPeers.Count() >= 2, TimeSpan.FromSeconds(5), "chain ready");

        await nodeA.BroadcastAsync(new NetworkMessage { Type = MessageTypes.TaskView });

        await WaitForAsync(() => receivedByC.Count > 0, TimeSpan.FromSeconds(5), "C receives");
        Assert.Equal("NODE-A", receivedByB.First().OriginMachine);
        Assert.Equal("NODE-A", receivedByC.First().OriginMachine);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ConnectedPeers accuracy
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectedPeers_AccurateAfterMultipleConnectsAndDisconnects()
    {
        int portHub = GetFreePort();
        var hub = CreateNode("HUB", portHub);
        hub.Start("pw", Array.Empty<(string, string, int)>());

        // Connect three peers.
        var peers = new List<(NetworkManager node, int port)>();
        for (int i = 0; i < 3; i++)
        {
            int p = GetFreePort();
            var peer = CreateNode($"PEER-{i}", p);
            peer.Start("pw", new[] { ("HUB", "127.0.0.1", portHub) });
            peers.Add((peer, p));
        }

        await WaitForAsync(() => hub.ConnectedPeers.Count() == 3, TimeSpan.FromSeconds(5), "all 3 connected");
        Assert.Equal(3, hub.ConnectedPeers.Count());

        // Disconnect PEER-1.
        peers[1].node.Stop();
        await WaitForAsync(() => hub.ConnectedPeers.Count() == 2, TimeSpan.FromSeconds(5), "PEER-1 disconnected");
        Assert.DoesNotContain("PEER-1", hub.ConnectedPeers);
        Assert.Contains("PEER-0", hub.ConnectedPeers);
        Assert.Contains("PEER-2", hub.ConnectedPeers);

        // Disconnect PEER-0.
        peers[0].node.Stop();
        await WaitForAsync(() => hub.ConnectedPeers.Count() == 1, TimeSpan.FromSeconds(5), "PEER-0 disconnected");
        Assert.Single(hub.ConnectedPeers);
        Assert.Contains("PEER-2", hub.ConnectedPeers);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // All message types
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(MessageTypes.SwitchLeft)]
    [InlineData(MessageTypes.SwitchRight)]
    [InlineData(MessageTypes.SwitchToDesktop)]
    [InlineData(MessageTypes.TaskView)]
    [InlineData(MessageTypes.StateUpdate)]
    public async Task AllMessageTypes_CanBeSentAndReceived(string messageType)
    {
        int portA = GetFreePort(), portB = GetFreePort();
        var nodeA = CreateNode("NODE-A", portA);
        var nodeB = CreateNode("NODE-B", portB);

        var received = new ConcurrentBag<NetworkMessage>();
        nodeB.MessageReceived += (_, msg) => received.Add(msg);

        nodeA.Start("pw", new[] { ("NODE-B", "127.0.0.1", portB) });
        nodeB.Start("pw", Array.Empty<(string, string, int)>());
        await WaitForAsync(() => nodeA.ConnectedPeers.Any(), TimeSpan.FromSeconds(5), "connected");

        await nodeA.BroadcastAsync(new NetworkMessage { Type = messageType });

        await WaitForAsync(() => received.Count > 0, TimeSpan.FromSeconds(5), $"{messageType} received");
        Assert.Equal(messageType, received.First().Type);
    }
}
