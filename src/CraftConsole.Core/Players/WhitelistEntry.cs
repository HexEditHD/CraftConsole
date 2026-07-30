using System.Text.Json.Serialization;

namespace CraftConsole.Core.Players;

/// <summary>An entry in the server's whitelist.json.</summary>
public record WhitelistEntry(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("name")] string Name);
