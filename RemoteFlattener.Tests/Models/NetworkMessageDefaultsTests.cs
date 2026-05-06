using System.Collections.Generic;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class NetworkMessageDefaultsTests
{
    // ── Default values ────────────────────────────────────────────────────

    [Fact]
    public void Type_Default_IsEmptyString()
    {
        // Type is used as a discriminator; null would crash switch statements.
        Assert.Equal(string.Empty, new NetworkMessage().Type);
    }

    [Fact]
    public void ProtocolVersion_Default_IsZero()
    {
        // A peer that sends no protocolVersion field deserializes as 0,
        // which the handshake correctly rejects (current protocol is version 1).
        Assert.Equal(0, new NetworkMessage().ProtocolVersion);
    }

    [Fact]
    public void Nullable_StringProperties_DefaultToNull()
    {
        var msg = new NetworkMessage();
        Assert.Null(msg.MachineName);
        Assert.Null(msg.Hmac);
        Assert.Null(msg.OriginMachine);
        Assert.Null(msg.MessageId);
        Assert.Null(msg.TargetMachine);
    }

    [Fact]
    public void Nullable_CollectionProperties_DefaultToNull()
    {
        var msg = new NetworkMessage();
        Assert.Null(msg.RdpPeers);
        Assert.Null(msg.RdpHostedServers);
        Assert.Null(msg.DesktopNames);
        Assert.Null(msg.WallpaperThumbnails);
    }

    [Fact]
    public void NumericProperties_DefaultToZero()
    {
        var msg = new NetworkMessage();
        Assert.Equal(0, msg.CurrentDesktop);
        Assert.Equal(0, msg.TotalDesktops);
    }

    [Fact]
    public void IsRdpServer_Default_IsFalse()
    {
        Assert.False(new NetworkMessage().IsRdpServer);
    }

    // ── Deserialization of old-format peers ───────────────────────────────

    [Fact]
    public void Deserialize_MissingProtocolVersion_DeserializesAsZero()
    {
        // Simulates a message from an old peer that doesn't send protocolVersion.
        // It must deserialize (not throw) and have ProtocolVersion = 0,
        // which the handshake will correctly reject.
        const string json = """{"type":"HELLO","machineName":"OLD-PC"}""";
        var msg = NetworkMessage.Deserialize(json);
        Assert.NotNull(msg);
        Assert.Equal(0, msg!.ProtocolVersion);
    }

    [Fact]
    public void Deserialize_WithProtocolVersion_IsPreserved()
    {
        const string json = """{"type":"HELLO","machineName":"NEW-PC","protocolVersion":1}""";
        var msg = NetworkMessage.Deserialize(json);
        Assert.NotNull(msg);
        Assert.Equal(1, msg!.ProtocolVersion);
    }
}
