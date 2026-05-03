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

    [JsonPropertyName("currentDesktop")]
    public int CurrentDesktop { get; set; }

    [JsonPropertyName("totalDesktops")]
    public int TotalDesktops { get; set; }

    [JsonPropertyName("isRdpServer")]
    public bool IsRdpServer { get; set; }

    [JsonPropertyName("rdpPeers")]
    public List<string>? RdpPeers { get; set; }

    /// <summary>Serializes this message to a newline-terminated JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this) + "\n";

    public static NetworkMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<NetworkMessage>(json);
}
