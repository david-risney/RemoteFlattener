using System;
using System.Collections.Generic;
using RemoteFlattener.Models;
using RemoteFlattener.Network;
using Xunit;

namespace RemoteFlattener.Tests.Network;

public class NetworkManagerInternalsTests
{
    // Uses the internal test constructor — no listener or connector is started.
    private static NetworkManager Make(string password = "test-password") =>
        new NetworkManager(password);

    // ── ComputeHmac ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeHmac_SameInputs_ProducesSameOutput()
    {
        var nm = Make();
        Assert.Equal(nm.ComputeHmac("MYPC"), nm.ComputeHmac("MYPC"));
    }

    [Fact]
    public void ComputeHmac_DifferentMachineNames_ProduceDifferentHmacs()
    {
        var nm = Make();
        Assert.NotEqual(nm.ComputeHmac("PC-A"), nm.ComputeHmac("PC-B"));
    }

    [Fact]
    public void ComputeHmac_ReturnsHexString()
    {
        var nm   = Make();
        var hmac = nm.ComputeHmac("MYPC");
        // HMAC-SHA256 = 32 bytes = 64 hex chars
        Assert.Equal(64, hmac.Length);
        Assert.Matches("^[0-9A-Fa-f]+$", hmac);
    }

    // ── VerifyHmac ────────────────────────────────────────────────────────

    [Fact]
    public void VerifyHmac_CorrectHmac_ReturnsTrue()
    {
        var nm   = Make("my-secret");
        var hmac = nm.ComputeHmac("SERVER1");
        Assert.True(nm.VerifyHmac("SERVER1", hmac));
    }

    [Fact]
    public void VerifyHmac_WrongPassword_ReturnsFalse()
    {
        var sender   = Make("correct-password");
        var receiver = Make("wrong-password");
        Assert.False(receiver.VerifyHmac("SERVER1", sender.ComputeHmac("SERVER1")));
    }

    [Fact]
    public void VerifyHmac_WrongMachineName_ReturnsFalse()
    {
        var nm = Make();
        Assert.False(nm.VerifyHmac("PC-B", nm.ComputeHmac("PC-A")));
    }

    [Fact]
    public void VerifyHmac_IsCaseInsensitive()
    {
        var nm   = Make();
        var hmac = nm.ComputeHmac("MYPC");
        Assert.True(nm.VerifyHmac("MYPC", hmac.ToLowerInvariant()));
        Assert.True(nm.VerifyHmac("MYPC", hmac.ToUpperInvariant()));
    }

    [Fact]
    public void VerifyHmac_TamperedHmac_ReturnsFalse()
    {
        var nm   = Make();
        var hmac = nm.ComputeHmac("MYPC");
        var tampered = hmac[0] == 'A' ? 'B' + hmac[1..] : 'A' + hmac[1..];
        Assert.False(nm.VerifyHmac("MYPC", tampered));
    }

    // ── MarkSeen ──────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_NewId_ReturnsTrue()
    {
        Assert.True(Make().MarkSeen("unique-id-1"));
    }

    [Fact]
    public void MarkSeen_SameIdTwice_ReturnsFalseSecondTime()
    {
        var nm = Make();
        Assert.True(nm.MarkSeen("dup"));
        Assert.False(nm.MarkSeen("dup"));
    }

    [Fact]
    public void MarkSeen_DifferentIds_AllReturnTrue()
    {
        var nm = Make();
        for (int i = 0; i < 50; i++)
            Assert.True(nm.MarkSeen($"id-{i}"));
    }

    [Fact]
    public void MarkSeen_AfterCapExceeded_IdCanBeReseenAfterClear()
    {
        // SeenIdCap = 1000. Once exceeded the set is fully cleared, opening a brief replay window.
        // This test documents the known behaviour so any future change to a proper LRU is caught.
        var nm = Make();
        nm.MarkSeen("first-id");
        for (int i = 0; i < 1001; i++) nm.MarkSeen($"filler-{i}");
        Assert.True(nm.MarkSeen("first-id"),
            "After cap overflow the set is fully cleared; 'first-id' should be accepted again. " +
            "If this fails, MarkSeen was upgraded to use a proper LRU \u2014 update this test.");
    }

    // ── PrepareForSend ────────────────────────────────────────────────────

    [Fact]
    public void PrepareForSend_SetsOriginMachineIfNull()
    {
        var nm  = Make();
        var msg = new NetworkMessage { Type = MessageTypes.StateUpdate };
        nm.PrepareForSend(msg);
        Assert.Equal(nm.LocalMachineName, msg.OriginMachine);
    }

    [Fact]
    public void PrepareForSend_DoesNotOverrideExistingOriginMachine()
    {
        var nm  = Make();
        var msg = new NetworkMessage { Type = MessageTypes.StateUpdate, OriginMachine = "RELAY-PC" };
        nm.PrepareForSend(msg);
        Assert.Equal("RELAY-PC", msg.OriginMachine);
    }

    [Fact]
    public void PrepareForSend_SetsMessageIdIfNull()
    {
        var nm  = Make();
        var msg = new NetworkMessage { Type = MessageTypes.StateUpdate };
        nm.PrepareForSend(msg);
        Assert.False(string.IsNullOrEmpty(msg.MessageId));
    }

    [Fact]
    public void PrepareForSend_DoesNotOverrideExistingMessageId()
    {
        var nm  = Make();
        var msg = new NetworkMessage { Type = MessageTypes.StateUpdate, MessageId = "fixed-id" };
        nm.PrepareForSend(msg);
        Assert.Equal("fixed-id", msg.MessageId);
    }

    [Fact]
    public void PrepareForSend_EachCallGeneratesUniqueMessageId()
    {
        var nm  = Make();
        var ids = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            var msg = new NetworkMessage { Type = MessageTypes.StateUpdate };
            nm.PrepareForSend(msg);
            ids.Add(msg.MessageId!);
        }
        Assert.Equal(20, ids.Count);
    }

    // ── FilterPeerMachines (static) ───────────────────────────────────────

    [Fact]
    public void FilterPeerMachines_RemovesSelf()
    {
        var result = NetworkManager.FilterPeerMachines(["SELF", "OTHER-PC"], "SELF");
        Assert.DoesNotContain("SELF", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("OTHER-PC", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterPeerMachines_RemovesSelfCaseInsensitive()
    {
        var result = NetworkManager.FilterPeerMachines(["self", "other"], "SELF");
        Assert.DoesNotContain("self", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterPeerMachines_RemovesEmptyAndWhitespace()
    {
        var result = NetworkManager.FilterPeerMachines(["", "  ", "\t", "VALID-PC"], "SELF");
        Assert.All(result, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.Single(result);
    }

    [Fact]
    public void FilterPeerMachines_DeduplicatesCaseInsensitive()
    {
        var result = NetworkManager.FilterPeerMachines(["PEER-A", "peer-a", "Peer-A", "PEER-B"], "SELF");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterPeerMachines_TrimsWhitespace()
    {
        var result = NetworkManager.FilterPeerMachines(["  PEER-A  ", " PEER-B"], "SELF");
        Assert.All(result, p => Assert.Equal(p.Trim(), p));
    }

    // ── LocalMachineName ─────────────────────────────────────────────────

    [Fact]
    public void LocalMachineName_IsNormalized()
    {
        var nm = Make();
        Assert.Equal(nm.LocalMachineName, nm.LocalMachineName.ToUpperInvariant());
        Assert.DoesNotContain(".", nm.LocalMachineName);
    }
}