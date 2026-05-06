using System.Collections.Generic;
using RemoteFlattener.Models;
using RemoteFlattener.Network;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class NetworkMessageTests
{
    // ── Serialize ─────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_AppendsSingleNewline()
    {
        var msg = new NetworkMessage { Type = "TEST" };
        var serialized = msg.Serialize();
        Assert.EndsWith("\n", serialized);
        Assert.DoesNotContain("\n\n", serialized);
    }

    [Fact]
    public void Serialize_ProducesValidJson()
    {
        var msg = new NetworkMessage { Type = "TEST", MachineName = "PC1" };
        var json = msg.Serialize().TrimEnd();
        // Should be deserializable
        var restored = NetworkMessage.Deserialize(json);
        Assert.NotNull(restored);
    }

    // ── Serialize / Deserialize round-trips ──────────────────────────────

    [Fact]
    public void RoundTrip_AllHandshakeFields()
    {
        var original = new NetworkMessage
        {
            Type            = MessageTypes.Hello,
            MachineName     = "TEST-PC",
            Hmac            = "DEADBEEF",
            ProtocolVersion = 42
        };
        var restored = Roundtrip(original);

        Assert.Equal(original.Type,            restored.Type);
        Assert.Equal(original.MachineName,     restored.MachineName);
        Assert.Equal(original.Hmac,            restored.Hmac);
        Assert.Equal(original.ProtocolVersion, restored.ProtocolVersion);
    }

    [Fact]
    public void RoundTrip_StateUpdateFields()
    {
        var original = new NetworkMessage
        {
            Type           = MessageTypes.StateUpdate,
            MachineName    = "DESKTOP-X",
            CurrentDesktop = 2,
            TotalDesktops  = 4,
            IsRdpServer    = true,
            RdpPeers       = new List<string> { "PEER1", "PEER2" },
            DesktopNames   = new List<string> { "Work", "Chat", "Media", "Dev" },
            OriginMachine  = "DESKTOP-X",
            MessageId      = "abc123"
        };
        var restored = Roundtrip(original);

        Assert.Equal(original.CurrentDesktop, restored.CurrentDesktop);
        Assert.Equal(original.TotalDesktops,  restored.TotalDesktops);
        Assert.Equal(original.IsRdpServer,    restored.IsRdpServer);
        Assert.Equal(original.RdpPeers,       restored.RdpPeers);
        Assert.Equal(original.DesktopNames,   restored.DesktopNames);
        Assert.Equal(original.OriginMachine,  restored.OriginMachine);
        Assert.Equal(original.MessageId,      restored.MessageId);
    }

    [Fact]
    public void RoundTrip_NullOptionalFields_RemainsNull()
    {
        var original = new NetworkMessage { Type = MessageTypes.StateUpdate };
        var restored = Roundtrip(original);

        Assert.Null(restored.MachineName);
        Assert.Null(restored.Hmac);
        Assert.Null(restored.RdpPeers);
        Assert.Null(restored.DesktopNames);
        Assert.Null(restored.TargetMachine);
        Assert.Null(restored.MessageId);
    }

    [Fact]
    public void RoundTrip_RdpHostedServers()
    {
        var original = new NetworkMessage
        {
            Type             = MessageTypes.StateUpdate,
            RdpHostedServers = new Dictionary<string, int> { ["SERVER1"] = 2, ["SERVER2"] = 3 }
        };
        var restored = Roundtrip(original);

        Assert.NotNull(restored.RdpHostedServers);
        Assert.Equal(2, restored.RdpHostedServers["SERVER1"]);
        Assert.Equal(3, restored.RdpHostedServers["SERVER2"]);
    }

    // ── Deserialize edge cases ────────────────────────────────────────────

    [Fact]
    public void Deserialize_MalformedJson_ReturnsNull()
    {
        var result = NetworkMessage.Deserialize("not valid json {{{{");
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsNull()
    {
        var result = NetworkMessage.Deserialize(string.Empty);
        Assert.Null(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static NetworkMessage Roundtrip(NetworkMessage msg)
    {
        var json = msg.Serialize().TrimEnd('\n');
        var restored = NetworkMessage.Deserialize(json);
        Assert.NotNull(restored);
        return restored;
    }
}
