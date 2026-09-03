using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemoteFlattener.Models;

namespace RemoteFlattener.Network;

/// <summary>Represents an authenticated, active connection to a remote peer.</summary>
internal sealed class PeerConnection : IDisposable
{
    public required string MachineName { get; init; }
    public required TcpClient Client { get; init; }
    public required StreamWriter Writer { get; init; }
    /// <summary>Guards Writer so concurrent broadcasts don't interleave JSON lines.</summary>
    public SemaphoreSlim WriteLock { get; } = new SemaphoreSlim(1, 1);

    public void Dispose()
    {
        WriteLock.Dispose();
        Writer.Dispose();
        Client.Dispose();
    }
}

/// <summary>
/// Manages TCP peer-to-peer connections.
/// Both listens for incoming connections and initiates outgoing connections to configured peers.
/// Authentication uses HMAC-SHA256 of (machineName + "RemoteFlattener") keyed with the shared password.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private const string AppSalt = "RemoteFlattener";
    /// <summary>
    /// Increment when making breaking changes to the wire format.
    /// Peers with a different protocol version will be rejected during handshake.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>TCP keep-alive interval in seconds.  Detects dead peers (e.g. laptop sleep).</summary>
    private const int KeepAliveSeconds = 15;

    private readonly NetworkManagerConfig _config;
    private readonly INetworkLogger _logger;

    private string _password = string.Empty;
    private List<string> _peerMachines = new();

    /// <summary>For testing: the filtered, deduplicated peer list built by Start().</summary>
    internal IReadOnlyList<string> PeerMachines => _peerMachines;

    /// <summary>
    /// For unit testing only: creates an instance with a preset password
    /// without starting the TCP listener or outgoing connectors.
    /// </summary>
    internal NetworkManager(string password) : this(password, new NetworkManagerConfig()) { }

    internal NetworkManager(string password, NetworkManagerConfig config) : this(config)
    {
        _password = password;
    }

    public NetworkManager() : this(new NetworkManagerConfig()) { }

    public NetworkManager(NetworkManagerConfig config)
    {
        _config = config;
        _logger = config.Logger;
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    // Rolling set of message IDs already processed — prevents relay loops.
    private readonly object _seenLock = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private const int SeenIdCap = 1000;

    private bool _disposed;

    /// <summary>The machine name of this instance (used in authentication and state messages).</summary>
    public string LocalMachineName => _config.LocalMachineName;

    /// <summary>TCP port this node listens on and connects to.</summary>
    public int Port => _config.Port;

    /// <summary>Machine names of currently authenticated, connected peers.</summary>
    public IEnumerable<string> ConnectedPeers =>
        _connections.Values.Select(c => c.MachineName);

    /// <summary>
    /// Returns a mapping of normalized peer machine name → remote IP address string
    /// for all currently connected peers.
    /// </summary>
    public Dictionary<string, string> GetPeerAddresses()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in _connections.Values)
        {
            try
            {
                var ep = conn.Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (ep != null)
                    result[conn.MachineName] = ep.Address.ToString();
            }
            catch { /* socket may be closed */ }
        }
        return result;
    }

    /// <summary>Raised when a message arrives from a peer (on a background thread).</summary>
    public event Action<string, NetworkMessage>? MessageReceived;
    /// <summary>Raised when a peer finishes authenticating (on a background thread).</summary>
    public event Action<string>? PeerConnected;
    /// <summary>Raised when a peer connection closes (on a background thread).</summary>
    public event Action<string>? PeerDisconnected;

    public void Start(string password, IEnumerable<string> peerMachines)
    {
        _password    = password;
        _peerMachines = FilterPeerMachines(peerMachines, LocalMachineName);

        _logger.Log($"NetworkManager starting.  Local node: {LocalMachineName}.  Listening on port {Port}.  Outgoing peers: [{string.Join(", ", _peerMachines)}]");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        foreach (var machine in _peerMachines)
            _ = Task.Run(() => OutgoingLoopAsync(machine, _cts.Token));
    }

    /// <summary>
    /// Starts this node with explicit peer endpoints.  Each tuple is (machineName, host, port).
    /// This allows tests to run multiple nodes on localhost with different ports.
    /// </summary>
    internal void Start(string password, IEnumerable<(string machineName, string host, int port)> peerEndpoints)
    {
        _password = password;
        _logger.Log($"NetworkManager starting.  Local node: {LocalMachineName}.  Listening on port {Port}.");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        foreach (var (machineName, host, port) in peerEndpoints)
            _ = Task.Run(() => OutgoingLoopAsync(machineName, host, port, _cts.Token));
    }

    /// <summary>
    /// Filters, trims, deduplicates, and removes self from a raw peer list.
    /// Extracted so it can be unit-tested without starting a TCP listener.
    /// </summary>
    internal static List<string> FilterPeerMachines(IEnumerable<string> peerMachines, string localMachineName) =>
        peerMachines
            .Select(m => m.Trim())
            .Where(m => !string.IsNullOrEmpty(m) &&
                        !m.Equals(localMachineName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Stop()
    {
        _logger.Log("NetworkManager stopping.");
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var conn in _connections.Values)
        {
            try
            {
                PeerDisconnected?.Invoke(conn.MachineName);
                conn.Dispose();
            }
            catch { }
        }
        _connections.Clear();
    }

    public async Task BroadcastAsync(NetworkMessage message)
    {
        PrepareForSend(message);
        MarkSeen(message.MessageId!);
        var json = message.Serialize();
        var tasks = _connections.Values
            .Select(c => SendLineAsync(c, json))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Send a message to a named machine.  If no direct connection exists, floods all peers
    /// so intermediary nodes can relay it onward.  Sets TargetMachine automatically.
    /// </summary>
    public async Task SendToPeerAsync(string machineName, NetworkMessage message)
    {
        message.TargetMachine = machineName;
        PrepareForSend(message);
        MarkSeen(message.MessageId!);

        if (_connections.TryGetValue(machineName, out var direct))
        {
            _logger.Log($"Sending {message.Type} directly to {machineName}.");
            await SendLineAsync(direct, message.Serialize());
        }
        else
        {
            // No direct link — flood all peers; one of them will relay it.
            _logger.Log($"No direct link to '{machineName}'; flooding {_connections.Count} peer(s) to relay.");
            var json = message.Serialize();
            var tasks = _connections.Values
                .Select(c => SendLineAsync(c, json))
                .ToArray();
            await Task.WhenAll(tasks);
        }
    }

    private static async Task SendLineAsync(PeerConnection conn, string json)
    {
        try
        {
            await conn.WriteLock.WaitAsync();
        }
        catch (ObjectDisposedException) { return; }
        try
        {
            await conn.Writer.WriteAsync(json);
            await conn.Writer.FlushAsync();
        }
        catch { /* Peer may have disconnected; will be noticed on next read */ }
        finally
        {
            try { conn.WriteLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    // ── Listener (incoming) ──────────────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _logger.Log($"Listener started on port {Port}.");
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
                _logger.Log($"Incoming TCP connection from {remoteIp}.");
                _ = Task.Run(() => HandleConnectionAsync(client, outgoing: false, remoteMachineHint: null, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Log($"Listener error: {ex.Message}"); }
        finally
        {
            _logger.Log("Listener stopped.");
            _listener?.Stop();
        }
    }

    /// <summary>
    /// Starts an outgoing connection loop to a dynamically discovered peer (e.g. from RDP
    /// auto-detection).  Unlike <see cref="Start"/>, this can be called after the network
    /// is already running and uses the raw <paramref name="connectionAddress"/> (typically
    /// an IP) for TCP so that cross-domain short-name DNS failures are avoided.
    /// No-ops if the peer is already covered by the static peer list or already connected.
    /// </summary>
    public void ConnectToPeer(string machineName, string connectionAddress, int? port = null)
    {
        if (_cts == null) return; // network not started
        var normalizedName = MachineInfo.NormalizeHostname(machineName);
        // Static peer list already has a loop for this machine — don't duplicate.
        if (_peerMachines.Any(p => p.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
            return;
        if (_connections.ContainsKey(normalizedName)) return;
        _logger.Log($"ConnectToPeer: starting connector for {machineName} via {connectionAddress}.");
        _ = Task.Run(() => OutgoingLoopAsync(machineName, connectionAddress, port ?? Port, _cts.Token));
    }

    // ── Outgoing connector (with reconnect) ──────────────────────────────────

    private async Task OutgoingLoopAsync(string machine, CancellationToken ct)
        => await OutgoingLoopAsync(machine, machine, Port, ct);

    private async Task OutgoingLoopAsync(string machineName, string connectionAddress, CancellationToken ct)
        => await OutgoingLoopAsync(machineName, connectionAddress, Port, ct);

    private async Task OutgoingLoopAsync(string machineName, string connectionAddress, int port, CancellationToken ct)
    {
        bool wasConnected = false;
        const int ReconnectDelayMs = 10_000;
        const int ConnectedPollMs  = 10_000;

        while (!ct.IsCancellationRequested)
        {
            // Skip if already connected (may have been initiated by the remote side first).
            // Normalize the machine name so that FQDNs (e.g. "server.corp.com") match the
            // short name ("SERVER") stored in _connections after handshake normalization.
            if (_connections.ContainsKey(MachineInfo.NormalizeHostname(machineName)))
            {
                wasConnected = true;
                await DelayOrCancel(ConnectedPollMs, ct);
                continue;
            }

            // Log differently when connecting via a separate address (e.g. IP for auto-detected peers).
            bool viaIp = !connectionAddress.Equals(machineName, StringComparison.OrdinalIgnoreCase);
            string displayTarget = viaIp
                ? $"{machineName} via {connectionAddress}:{port}"
                : $"{machineName}:{port}";

            _logger.Log($"Outgoing: attempting connection to {displayTarget}...");
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(connectionAddress, port, ct);
                _logger.Log($"Outgoing: TCP connected to {displayTarget}.  Starting handshake.");
                // HandleConnectionAsync will register in _connections; when it exits, loop retries.
                await HandleConnectionAsync(client, outgoing: true, remoteMachineHint: machineName, ct);
                // Connection was established then dropped — retry after a delay.
                wasConnected = true;
                _logger.Log($"Outgoing: connection to {displayTarget} lost. Will retry in {ReconnectDelayMs / 1000} s.");
                await DelayOrCancel(ReconnectDelayMs, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!wasConnected)
                {
                    // First attempt failed — peer will connect to us via the listener.
                    _logger.Log($"Outgoing: failed to connect to {displayTarget}: {ex.Message}. Peer will connect to us when ready.");
                    return;
                }
                // Reconnection attempt after a previous connection — retry with delay.
                _logger.Log($"Outgoing: failed to reconnect to {displayTarget}: {ex.Message}. Retrying in {ReconnectDelayMs / 1000} s.");
                await DelayOrCancel(ReconnectDelayMs, ct);
            }
        }
    }

    // ── Shared connection handler ────────────────────────────────────────────

    private async Task HandleConnectionAsync(
        TcpClient client, bool outgoing, string? remoteMachineHint, CancellationToken ct)
    {
        string? remoteMachine = null;
        bool registered = false;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false) { AutoFlush = false };

        try
        {
            if (outgoing)
            {
                // Send HELLO then wait for HELLO_ACK.
                var hello = BuildHello();
                await writer.WriteAsync(hello.Serialize());
                await writer.FlushAsync();

                var ackLine = await reader.ReadLineAsync(ct);
                if (ackLine == null) { _logger.Log($"Outgoing [{remoteMachineHint}]: connection closed before HELLO_ACK."); return; }
                var ack = NetworkMessage.Deserialize(ackLine);
                // Verify server also knows the password via its HMAC.
                if (ack?.Type != MessageTypes.HelloAck ||
                    string.IsNullOrEmpty(ack.MachineName) ||
                    string.IsNullOrEmpty(ack.Hmac) ||
                    !VerifyHmac(ack.MachineName, ack.Hmac))
                {
                    _logger.Log($"Outgoing [{remoteMachineHint}]: authentication failed (bad {MessageTypes.HelloAck}).");
                    return;
                }
                if (ack.ProtocolVersion != ProtocolVersion)
                {
                    _logger.Log($"Outgoing [{remoteMachineHint}]: incompatible protocol version (ours={ProtocolVersion}, theirs={ack.ProtocolVersion}). Update RemoteFlattener on both machines.");
                    return;
                }

                remoteMachine = MachineInfo.NormalizeHostname(remoteMachineHint ?? ack.MachineName);
            }
            else
            {
                // Wait for HELLO then send HELLO_ACK with our own HMAC.
                var helloLine = await reader.ReadLineAsync(ct);
                if (helloLine == null) { _logger.Log("Incoming: connection closed before HELLO."); return; }
                var hello = NetworkMessage.Deserialize(helloLine);
                if (hello?.Type != MessageTypes.Hello ||
                    string.IsNullOrEmpty(hello.MachineName) ||
                    string.IsNullOrEmpty(hello.Hmac) ||
                    !VerifyHmac(hello.MachineName, hello.Hmac))
                {
                    _logger.Log("Incoming: authentication failed (bad HELLO or wrong password).");
                    return;
                }
                if (hello.ProtocolVersion != ProtocolVersion)
                {
                    _logger.Log($"Incoming [{hello.MachineName}]: incompatible protocol version (ours={ProtocolVersion}, theirs={hello.ProtocolVersion}). Update RemoteFlattener on both machines.");
                    return;
                }

                remoteMachine = MachineInfo.NormalizeHostname(hello.MachineName);
                var ack = BuildHello();
                ack.Type = MessageTypes.HelloAck;
                await writer.WriteAsync(ack.Serialize());
                await writer.FlushAsync();
            }

            // Enable TCP keep-alive so dead peers (e.g. laptop sleep) are detected promptly.
            EnableKeepAlive(client);

            // Register connection; drop duplicate (first-in wins).
            var conn = new PeerConnection { MachineName = remoteMachine, Client = client, Writer = writer };
            if (!_connections.TryAdd(remoteMachine, conn))
            {
                _logger.Log($"Duplicate connection for {remoteMachine} — dropping.");
                conn.Dispose();
                return;
            }
            registered = true;

            _logger.Log($"Peer {remoteMachine} authenticated and connected ({(outgoing ? "outgoing" : "incoming")}).");
            PeerConnected?.Invoke(remoteMachine);

            // Read messages until the connection closes.
            string? line;
            while (!ct.IsCancellationRequested &&
                   (line = await reader.ReadLineAsync(ct)) != null)
            {
                var msg = NetworkMessage.Deserialize(line);
                if (msg == null) continue;

                // Ensure every message has an ID (backward compat with older nodes).
                if (string.IsNullOrEmpty(msg.MessageId))
                    msg.MessageId = Guid.NewGuid().ToString("N");

                if (!MarkSeen(msg.MessageId))
                {
                    // Already processed — drop silently to break relay loops.
                    continue;
                }

                // Should this node act on the message?
                bool isForMe = msg.TargetMachine == null ||
                               msg.TargetMachine.Equals(LocalMachineName, StringComparison.OrdinalIgnoreCase);

                if (isForMe)
                {
                    _logger.Log($"Received {msg.Type} from {remoteMachine} (origin: {msg.OriginMachine ?? remoteMachine}).");
                    MessageReceived?.Invoke(remoteMachine, msg);
                }

                // Relay to all other directly connected peers (mesh forwarding).
                if (_connections.Count > 1)
                {
                    var relayJson = msg.Serialize();
                    var relay = _connections.Values
                        .Where(c => !c.MachineName.Equals(remoteMachine, StringComparison.OrdinalIgnoreCase))
                        .Select(c => SendLineAsync(c, relayJson))
                        .ToArray();
                    if (relay.Length > 0)
                        await Task.WhenAll(relay);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Log($"Connection error [{remoteMachine ?? remoteMachineHint ?? "unknown"}]: {ex.Message}"); }
        finally
        {
            reader.Dispose();
            // Only remove from _connections if this handler was the one that registered.
            if (registered && remoteMachine != null && _connections.TryRemove(remoteMachine, out var removed))
            {
                removed.Dispose();
                _logger.Log($"Peer {remoteMachine} disconnected.");
                PeerDisconnected?.Invoke(remoteMachine);
            }
            else
            {
                writer.Dispose();
                try { client.Close(); } catch { }
            }
        }
    }

    // ── Authentication helpers ───────────────────────────────────────────────

    /// <summary>
    /// Stamps OriginMachine and a fresh MessageId on the message if not already set.
    /// Call this before sending any locally-originated message.
    /// </summary>
    internal void PrepareForSend(NetworkMessage message)
    {
        message.OriginMachine ??= LocalMachineName;
        message.MessageId     ??= Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Adds the ID to the seen set.  Returns true if it was new (should be processed/relayed),
    /// false if it was already known (duplicate — drop).
    /// </summary>
    internal bool MarkSeen(string id)
    {
        lock (_seenLock)
        {
            if (!_seenMessageIds.Add(id))
                return false;
            if (_seenMessageIds.Count > SeenIdCap)
                _seenMessageIds.Clear();
            return true;
        }
    }

    private NetworkMessage BuildHello()
    {
        return new NetworkMessage
        {
            Type            = MessageTypes.Hello,
            MachineName     = LocalMachineName,
            Hmac            = ComputeHmac(LocalMachineName),
            ProtocolVersion = ProtocolVersion
        };
    }

    internal string ComputeHmac(string machineName)
    {
        var key  = Encoding.UTF8.GetBytes(_password);
        var data = Encoding.UTF8.GetBytes(machineName + AppSalt);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    internal bool VerifyHmac(string machineName, string provided)
    {
        var expected = ComputeHmac(machineName);
        return string.Equals(expected, provided, StringComparison.OrdinalIgnoreCase);
    }

    // ── TCP keep-alive ──────────────────────────────────────────────────────

    private static void EnableKeepAlive(TcpClient client)
    {
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        // Time before first probe, interval between probes, and max failed probes.
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveSeconds);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveSeconds);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    private static async Task DelayOrCancel(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}
