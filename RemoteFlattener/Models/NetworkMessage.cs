using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteFlattener.Models;

public class NetworkMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    /// <summary>HMAC-SHA256 hex string used in HELLO messages for authentication.</summary>
    [JsonPropertyName("hmac")]
    public string? Hmac { get; set; }

    /// <summary>Wire protocol version exchanged during handshake. Peers with different versions are rejected.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("currentDesktop")]
    public int CurrentDesktop { get; set; }

    [JsonPropertyName("totalDesktops")]
    public int TotalDesktops { get; set; }

    [JsonPropertyName("isRdpServer")]
    public bool IsRdpServer { get; set; }

    [JsonPropertyName("rdpPeers")]
    public List<string>? RdpPeers { get; set; }

    /// <summary>
    /// Sent by RDP-server machines in remote sessions (e.g. Cloud DevBox).
    /// Contains the <c>CLIENTNAME</c> environment variable — the hostname of the
    /// machine running the RDP client (mstsc/msrdc) that is connected to this server.
    /// Allows the client to identify this peer as "my DevBox" when port-3389 TCP
    /// scanning is unavailable (WebRTC transport).
    /// </summary>
    [JsonPropertyName("rdpClientName")]
    public string? RdpClientName { get; set; }

    /// <summary>
    /// Sent by RDP-client machines only.  Maps server machine name → the local desktop index
    /// on which that server's mstsc window lives.  Allows every node in the mesh to place
    /// servers under the correct desktop row in the Network Tree, not just the client itself.
    /// </summary>
    [JsonPropertyName("rdpHostedServers")]
    public Dictionary<string, int>? RdpHostedServers { get; set; }

    [JsonPropertyName("desktopNames")]
    public List<string>? DesktopNames { get; set; }

    /// <summary>Base64-encoded JPEG thumbnails, one entry per desktop (empty string = no thumbnail).</summary>
    [JsonPropertyName("wallpaperThumbnails")]
    public List<string>? WallpaperThumbnails { get; set; }

    /// <summary>Machine that originally created this message.  Set by the sender; preserved by relays.</summary>
    [JsonPropertyName("originMachine")]
    public string? OriginMachine { get; set; }

    /// <summary>Unique ID used to detect and drop duplicate messages in the mesh.</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    /// <summary>If set, only the named machine should act on this message (others relay it onward).</summary>
    [JsonPropertyName("targetMachine")]
    public string? TargetMachine { get; set; }

    /// <summary>Serializes this message to a newline-terminated JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this) + "\n";

    public static NetworkMessage? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<NetworkMessage>(json); }
        catch { return null; }
    }
}
