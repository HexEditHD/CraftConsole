namespace CraftConsole.Core.Models;

public class ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ConnectionMode Mode { get; set; } = ConnectionMode.Managed;

    // ── Managed (the panel launches and owns the process) ──────────────────
    // Defaulted rather than required: an Rcon-mode profile has none of these.
    // ProfilesService validates the fields each mode actually needs.
    public string JarPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string JavaPath { get; set; } = "java";
    public int MinRamMb { get; set; } = 512;
    public int MaxRamMb { get; set; } = 2048;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string JvmArguments { get; set; } = string.Empty;
    public ServerType Type { get; set; } = ServerType.Paper;

    // ── Rcon (the panel connects to a server it did not start) ─────────────
    // No password here — this type is serialized straight into HTTP responses
    // and SSE status frames, so a password would ship to every connected
    // browser. See RconSecretStore.
    public string RconHost { get; set; } = string.Empty;
    public int RconPort { get; set; } = 25575;
}
