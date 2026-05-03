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
/// Manages TCP peer-to-peer connections on port 8765.
/// Both listens for incoming connections and initiates outgoing connections to configured peers.
/// Authentication uses HMAC-SHA256 of (machineName + "RemoteFlattener") keyed with the shared password.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private const int Port = 8765;
    private const string AppSalt = "RemoteFlattener";

    private string _password = string.Empty;
    private List<string> _peerMachines = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>The machine name of this instance (used in authentication and state messages).</summary>
    public string LocalMachineName { get; } = Environment.MachineName;

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
        _password = password;
        _peerMachines = peerMachines
            .Select(m => m.Trim())
            .Where(m => !string.IsNullOrEmpty(m) &&
                        !m.Equals(LocalMachineName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        foreach (var machine in _peerMachines)
            _ = Task.Run(() => OutgoingLoopAsync(machine, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var conn in _connections.Values)
        {
            try { conn.Dispose(); } catch { }
        }
        _connections.Clear();
    }

    public async Task BroadcastAsync(NetworkMessage message)
    {
        var json = message.Serialize();
        var tasks = _connections.Values
            .Select(c => SendLineAsync(c, json))
            .ToArray();
        await Task.WhenAll(tasks);
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
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleConnectionAsync(client, outgoing: false, remoteMachineHint: null, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { _listener?.Stop(); }
    }

    // ── Outgoing connector (with reconnect) ──────────────────────────────────

    private async Task OutgoingLoopAsync(string machine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Skip if already connected (may have been initiated by the remote side first).
            if (_connections.ContainsKey(machine))
            {
                await DelayOrCancel(5_000, ct);
                continue;
            }

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(machine, Port, ct);
                // HandleConnectionAsync will register in _connections; when it exits, loop retries.
                await HandleConnectionAsync(client, outgoing: true, remoteMachineHint: machine, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* connection refused, name not resolved, etc. */ }

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
                if (ackLine == null) return;
                var ack = NetworkMessage.Deserialize(ackLine);
                // Verify server also knows the password via its HMAC.
                if (ack?.Type != "HELLO_ACK" ||
                    string.IsNullOrEmpty(ack.MachineName) ||
                    string.IsNullOrEmpty(ack.Hmac) ||
                    !VerifyHmac(ack.MachineName, ack.Hmac))
                    return;

                remoteMachine = remoteMachineHint ?? ack.MachineName;
            }
            else
            {
                // Wait for HELLO then send HELLO_ACK with our own HMAC.
                var helloLine = await reader.ReadLineAsync(ct);
                if (helloLine == null) return;
                var hello = NetworkMessage.Deserialize(helloLine);
                if (hello?.Type != "HELLO" ||
                    string.IsNullOrEmpty(hello.MachineName) ||
                    string.IsNullOrEmpty(hello.Hmac) ||
                    !VerifyHmac(hello.MachineName, hello.Hmac))
                    return;

                remoteMachine = hello.MachineName;
                var ack = BuildHello();
                ack.Type = "HELLO_ACK";
                await writer.WriteAsync(ack.Serialize());
                await writer.FlushAsync();
            }

            // Register connection; drop duplicate (first-in wins).
            var conn = new PeerConnection { MachineName = remoteMachine, Client = client, Writer = writer };
            if (!_connections.TryAdd(remoteMachine, conn))
            {
                conn.Dispose();
                return;
            }

            PeerConnected?.Invoke(remoteMachine);

            // Read messages until the connection closes.
            string? line;
            while (!ct.IsCancellationRequested &&
                   (line = await reader.ReadLineAsync(ct)) != null)
            {
                var msg = NetworkMessage.Deserialize(line);
                if (msg != null)
                    MessageReceived?.Invoke(remoteMachine, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            reader.Dispose();
            // conn.Dispose() closes writer and client; fall back if conn was never registered.
            if (remoteMachine != null && _connections.TryRemove(remoteMachine, out var removed))
            {
                removed.Dispose();
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

    private NetworkMessage BuildHello()
    {
        return new NetworkMessage
        {
            Type = "HELLO",
            MachineName = LocalMachineName,
            Hmac = ComputeHmac(LocalMachineName)
        };
    }

    private string ComputeHmac(string machineName)
    {
        var key  = Encoding.UTF8.GetBytes(_password);
        var data = Encoding.UTF8.GetBytes(machineName + AppSalt);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }

    private bool VerifyHmac(string machineName, string provided)
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
