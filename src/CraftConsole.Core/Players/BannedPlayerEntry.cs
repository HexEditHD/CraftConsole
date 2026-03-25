using System.Text.Json.Serialization;

namespace CraftConsole.Core.Players;

public record BannedPlayerEntry(
    [property: JsonPropertyName("name")]    string Name,
    [property: JsonPropertyName("uuid")]    string Uuid,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("source")]  string Source,
    [property: JsonPropertyName("expires")] string Expires,
    [property: JsonPropertyName("reason")]  string Reason);
