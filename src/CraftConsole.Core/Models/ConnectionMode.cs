namespace CraftConsole.Core.Models;

public enum ConnectionMode
{
    /// <summary>The panel launches and owns the Minecraft server process.</summary>
    Managed,

    /// <summary>The panel connects to an already-running server via RCON.</summary>
    Rcon,
}
