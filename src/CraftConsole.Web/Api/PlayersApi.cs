using System.Text.Json;
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
