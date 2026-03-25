using System.Text.Json.Serialization;

namespace CraftConsole.Core.Players;

public record BannedIpEntry(
    [property: JsonPropertyName("ip")]      string Ip,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("source")]  string Source,
    [property: JsonPropertyName("expires")] string Expires,
    [property: JsonPropertyName("reason")]  string Reason);
