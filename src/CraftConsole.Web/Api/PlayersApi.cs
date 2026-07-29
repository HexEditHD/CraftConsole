using System.Text.Json;
using CraftConsole.Core.Models;
using CraftConsole.Core.Players;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

public static class PlayersApi
{
    public record PlayerActionRequest(string Target, string? Reason);

    public static void MapPlayersApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/players", (ServerSupervisor sup) =>
            Results.Json(new { Players = sup.PlayersSnapshot() }, Json.Options));

        app.MapGet("/api/players/banned", (ServerSupervisor sup) =>
            Results.Json(ReadServerJson<BannedPlayerEntry>(sup, "banned-players.json"), Json.Options));

        app.MapGet("/api/players/banned-ips", (ServerSupervisor sup) =>
            Results.Json(ReadServerJson<BannedIpEntry>(sup, "banned-ips.json"), Json.Options));

        app.MapPost("/api/players/kick", (PlayerActionRequest req, ServerSupervisor sup)
            => RunPlayerCommand(sup, "kick", req.Target, req.Reason));

        app.MapPost("/api/players/ban", (PlayerActionRequest req, ServerSupervisor sup)
            => RunPlayerCommand(sup, "ban", req.Target, req.Reason));

        app.MapPost("/api/players/ban-ip", (PlayerActionRequest req, ServerSupervisor sup)
            => RunPlayerCommand(sup, "ban-ip", req.Target, req.Reason));

        app.MapPost("/api/players/pardon", (PlayerActionRequest req, ServerSupervisor sup)
            => RunPlayerCommand(sup, "pardon", req.Target, null));

        app.MapPost("/api/players/pardon-ip", (PlayerActionRequest req, ServerSupervisor sup)
            => RunPlayerCommand(sup, "pardon-ip", req.Target, null));

        // ── Whitelist ─────────────────────────────────────────────────────
        app.MapGet("/api/players/whitelist", (ServerSupervisor sup) => Results.Json(new
        {
            Entries = ReadServerJson<WhitelistEntry>(sup, "whitelist.json"),
            // white-list is the historical spelling and is still what the file uses.
            Enabled = ReadServerProperty(sup, "white-list") == "true",
        }, Json.Options));

        app.MapPost("/api/players/whitelist/add", (PlayerActionRequest req, ServerSupervisor sup)
            => RunWhitelistCommand(sup, $"whitelist add {req.Target}", req.Target));

        app.MapPost("/api/players/whitelist/remove", (PlayerActionRequest req, ServerSupervisor sup)
            => RunWhitelistCommand(sup, $"whitelist remove {req.Target}", req.Target));

        app.MapPost("/api/players/whitelist/on", (ServerSupervisor sup)
            => RunWhitelistCommand(sup, "whitelist on", null));

        app.MapPost("/api/players/whitelist/off", (ServerSupervisor sup)
            => RunWhitelistCommand(sup, "whitelist off", null));

        app.MapPost("/api/players/whitelist/reload", (ServerSupervisor sup)
            => RunWhitelistCommand(sup, "whitelist reload", null));
    }

    private static async Task<IResult> RunWhitelistCommand(
        ServerSupervisor sup, string command, string? target)
    {
        if (target is not null && string.IsNullOrWhiteSpace(target))
            return Results.BadRequest(new { Message = "A player name is required." });

        // Whitelist changes go through the server so it rewrites whitelist.json
        // and applies them live; editing the file directly would need a reload
        // and would be lost if the server rewrote it first.
        if (sup.Status is not (ServerStatus.Running or ServerStatus.Starting))
            return Results.BadRequest(new
            {
                Message = "The server must be running to change the whitelist."
            });

        await sup.SendCommandAsync(command);
        return Results.NoContent();
    }

    /// <summary>Reads a single key from the server's server.properties.</summary>
    private static string? ReadServerProperty(ServerSupervisor sup, string key)
    {
        var workingDir = sup.ActiveProfile?.WorkingDirectory;
        if (workingDir is null) return null;

        try
        {
            var path = Path.Combine(workingDir, "server.properties");
            if (!File.Exists(path)) return null;

            var prefix = key + "=";
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line[prefix.Length..].Trim();
            }
        }
        catch { /* unreadable — treat as unset */ }

        return null;
    }

    private static async Task<IResult> RunPlayerCommand(
        ServerSupervisor sup, string verb, string target, string? reason)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Results.BadRequest(new { Message = "Target is required." });

        var command = string.IsNullOrWhiteSpace(reason)
            ? $"{verb} {target}"
            : $"{verb} {target} {reason.Trim()}";

        await sup.SendCommandAsync(command);
        return Results.NoContent();
    }

    private static List<T> ReadServerJson<T>(ServerSupervisor sup, string fileName)
    {
        var workingDir = sup.ActiveProfile?.WorkingDirectory;
        if (workingDir is null) return [];

        try
        {
            var path = Path.Combine(workingDir, fileName);
            if (!File.Exists(path)) return [];
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<T>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return []; // file may be locked or malformed
        }
    }
}
