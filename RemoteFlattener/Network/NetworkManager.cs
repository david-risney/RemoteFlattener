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
using RemoteFlattener.Logging;
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
/// Manages TCP peer-to-peer connections on port 8765.
/// Both listens for incoming connections and initiates outgoing connections to configured peers.
/// Authentication uses HMAC-SHA256 of (machineName + "RemoteFlattener") keyed with the shared password.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private const int Port = 8765;
    private const string AppSalt = "RemoteFlattener";
    /// <summary>
    /// Increment when making breaking changes to the wire format.
    /// Peers with a different protocol version will be rejected during handshake.
    /// </summary>
    public const int ProtocolVersion = 1;

    private string _password = string.Empty;
    private List<string> _peerMachines = new();

    /// <summary>For testing: the filtered, deduplicated peer list built by Start().</summary>
    internal IReadOnlyList<string> PeerMachines => _peerMachines;

    /// <summary>
    /// For unit testing only: creates an instance with a preset password
    /// without starting the TCP listener or outgoing connectors.
    /// </summary>
    internal NetworkManager(string password) { _password = password; }

    public NetworkManager() { }
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    // Rolling set of message IDs already processed — prevents relay loops.
    private readonly object _seenLock = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private const int SeenIdCap = 1000;

    private bool _disposed;

    /// <summary>The machine name of this instance (used in authentication and state messages).</summary>
    public string LocalMachineName { get; } = MachineInfo.NormalizeHostname(Environment.MachineName);

    /// <summary>Machine names of currently authenticated, connected peers.</summary>
    public IEnumerable<string> ConnectedPeers =>
        _connections.Values.Select(c => c.MachineName);

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

        AppLogger.Log($"NetworkManager starting.  Listening on port {Port}.  Outgoing peers: [{string.Join(", ", _peerMachines)}]");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        foreach (var machine in _peerMachines)
            _ = Task.Run(() => OutgoingLoopAsync(machine, _cts.Token));
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
        AppLogger.Log("NetworkManager stopping.");
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
            AppLogger.Log($"Sending {message.Type} directly to {machineName}.");
            await SendLineAsync(direct, message.Serialize());
        }
        else
        {
            // No direct link — flood all peers; one of them will relay it.
            AppLogger.Log($"No direct link to '{machineName}'; flooding {_connections.Count} peer(s) to relay.");
            var json = message.Serialize();
            var tasks = _connections.Values
                .Select(c => SendLineAsync(c, json))
                .ToArray();
            await Task.WhenAll(tasks);
        }
    }

    private static async Task SendLineAsync(PeerConnection conn, string json)
    {
        await conn.WriteLock.WaitAsync();
        try
        {
            await conn.Writer.WriteAsync(json);
            await conn.Writer.FlushAsync();
        }
        catch { /* Peer may have disconnected; will be noticed on next read */ }
        finally { conn.WriteLock.Release(); }
    }

    // ── Listener (incoming) ──────────────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            AppLogger.Log($"Listener started on port {Port}.");
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
                AppLogger.Log($"Incoming TCP connection from {remoteIp}.");
                _ = Task.Run(() => HandleConnectionAsync(client, outgoing: false, remoteMachineHint: null, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLogger.Log($"Listener error: {ex.Message}"); }
        finally
        {
            AppLogger.Log("Listener stopped.");
            _listener?.Stop();
        }
    }

    // ── Outgoing connector (with reconnect) ──────────────────────────────────

    private async Task OutgoingLoopAsync(string machine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Skip if already connected (may have been initiated by the remote side first).
            // Normalize the machine name so that FQDNs (e.g. "server.corp.com") match the
            // short name ("SERVER") stored in _connections after handshake normalization.
            if (_connections.ContainsKey(MachineInfo.NormalizeHostname(machine)))
            {
                await DelayOrCancel(5_000, ct);
                continue;
            }

            AppLogger.Log($"Outgoing: attempting connection to {machine}:{Port}...");
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(machine, Port, ct);
                AppLogger.Log($"Outgoing: TCP connected to {machine}:{Port}.  Starting handshake.");
                // HandleConnectionAsync will register in _connections; when it exits, loop retries.
                await HandleConnectionAsync(client, outgoing: true, remoteMachineHint: machine, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLogger.Log($"Outgoing: failed to connect to {machine}: {ex.Message}. Retrying in 5 s.");
            }

            await DelayOrCancel(5_000, ct);
        }
    }

    // ── Shared connection handler ────────────────────────────────────────────

    private async Task HandleConnectionAsync(
        TcpClient client, bool outgoing, string? remoteMachineHint, CancellationToken ct)
    {
        string? remoteMachine = null;
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
                if (ackLine == null) { AppLogger.Log($"Outgoing [{remoteMachineHint}]: connection closed before HELLO_ACK."); return; }
                var ack = NetworkMessage.Deserialize(ackLine);
                // Verify server also knows the password via its HMAC.
                if (ack?.Type != MessageTypes.HelloAck ||
                    string.IsNullOrEmpty(ack.MachineName) ||
                    string.IsNullOrEmpty(ack.Hmac) ||
                    !VerifyHmac(ack.MachineName, ack.Hmac))
                {
                    AppLogger.Log($"Outgoing [{remoteMachineHint}]: authentication failed (bad {MessageTypes.HelloAck}).");
                    return;
                }
                if (ack.ProtocolVersion != ProtocolVersion)
                {
                    AppLogger.Log($"Outgoing [{remoteMachineHint}]: incompatible protocol version (ours={ProtocolVersion}, theirs={ack.ProtocolVersion}). Update RemoteFlattener on both machines.");
                    return;
                }

                remoteMachine = MachineInfo.NormalizeHostname(remoteMachineHint ?? ack.MachineName);
            }
            else
            {
                // Wait for HELLO then send HELLO_ACK with our own HMAC.
                var helloLine = await reader.ReadLineAsync(ct);
                if (helloLine == null) { AppLogger.Log("Incoming: connection closed before HELLO."); return; }
                var hello = NetworkMessage.Deserialize(helloLine);
                if (hello?.Type != MessageTypes.Hello ||
                    string.IsNullOrEmpty(hello.MachineName) ||
                    string.IsNullOrEmpty(hello.Hmac) ||
                    !VerifyHmac(hello.MachineName, hello.Hmac))
                {
                    AppLogger.Log("Incoming: authentication failed (bad HELLO or wrong password).");
                    return;
                }
                if (hello.ProtocolVersion != ProtocolVersion)
                {
                    AppLogger.Log($"Incoming [{hello.MachineName}]: incompatible protocol version (ours={ProtocolVersion}, theirs={hello.ProtocolVersion}). Update RemoteFlattener on both machines.");
                    return;
                }

                remoteMachine = MachineInfo.NormalizeHostname(hello.MachineName);
                var ack = BuildHello();
                ack.Type = MessageTypes.HelloAck;
                await writer.WriteAsync(ack.Serialize());
                await writer.FlushAsync();
            }

            // Register connection; drop duplicate (first-in wins).
            var conn = new PeerConnection { MachineName = remoteMachine, Client = client, Writer = writer };
            if (!_connections.TryAdd(remoteMachine, conn))
            {
                AppLogger.Log($"Duplicate connection for {remoteMachine} — dropping.");
                conn.Dispose();
                return;
            }

            AppLogger.Log($"Peer {remoteMachine} authenticated and connected ({(outgoing ? "outgoing" : "incoming")}).");
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
                    AppLogger.Log($"Received {msg.Type} from {remoteMachine} (origin: {msg.OriginMachine ?? remoteMachine}).");
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
        catch (Exception ex) { AppLogger.Log($"Connection error [{remoteMachine ?? remoteMachineHint ?? "unknown"}]: {ex.Message}"); }
        finally
        {
            reader.Dispose();
            // conn.Dispose() closes writer and client; fall back if conn was never registered.
            if (remoteMachine != null && _connections.TryRemove(remoteMachine, out var removed))
            {
                removed.Dispose();
                AppLogger.Log($"Peer {remoteMachine} disconnected.");
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
