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
        app.MapGet("/api/servers/{id:guid}/players", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(new { Players = sup.PlayersSnapshot() }, Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/players", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(new { Players = sup?.PlayersSnapshot() ?? [] }, Json.Options);
        }).RequireRole(Role.Operator);

        app.MapGet("/api/servers/{id:guid}/players/banned", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(BannedSnapshot(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/players/banned", async (ProfilesService profiles, ServerRegistry registry) =>
            Results.Json(BannedSnapshot(await ServerScope.ResolveActiveAsync(profiles, registry)), Json.Options))
            .RequireRole(Role.Operator);

        app.MapGet("/api/servers/{id:guid}/players/banned-ips", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(BannedIpsSnapshot(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/players/banned-ips", async (ProfilesService profiles, ServerRegistry registry) =>
            Results.Json(BannedIpsSnapshot(await ServerScope.ResolveActiveAsync(profiles, registry)), Json.Options))
            .RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/kick", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunPlayerCommand(sup, "kick", req.Target, req.Reason)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/kick", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunPlayerCommandOnActive(profiles, registry, "kick", req.Target, req.Reason)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/ban", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunPlayerCommand(sup, "ban", req.Target, req.Reason)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/ban", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunPlayerCommandOnActive(profiles, registry, "ban", req.Target, req.Reason)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/ban-ip", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunPlayerCommand(sup, "ban-ip", req.Target, req.Reason)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/ban-ip", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunPlayerCommandOnActive(profiles, registry, "ban-ip", req.Target, req.Reason)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/pardon", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunPlayerCommand(sup, "pardon", req.Target, null)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/pardon", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunPlayerCommandOnActive(profiles, registry, "pardon", req.Target, null)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/pardon-ip", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunPlayerCommand(sup, "pardon-ip", req.Target, null)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/pardon-ip", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunPlayerCommandOnActive(profiles, registry, "pardon-ip", req.Target, null)).RequireRole(Role.Operator);

        // ── Whitelist ─────────────────────────────────────────────────────
        // Only the LIST is gated on local file access — add/remove/on/off/reload
        // are plain commands below and work over RCON exactly like moderation does.
        app.MapGet("/api/servers/{id:guid}/players/whitelist", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(WhitelistSnapshot(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/players/whitelist", async (ProfilesService profiles, ServerRegistry registry) =>
            Results.Json(WhitelistSnapshot(await ServerScope.ResolveActiveAsync(profiles, registry)), Json.Options))
            .RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/whitelist/add", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunWhitelistCommand(sup, $"whitelist add {req.Target}", req.Target)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/whitelist/add", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunWhitelistCommandOnActive(profiles, registry, $"whitelist add {req.Target}", req.Target)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/whitelist/remove", async (Guid id, PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunWhitelistCommand(sup, $"whitelist remove {req.Target}", req.Target)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/whitelist/remove", async (PlayerActionRequest req, ProfilesService profiles, ServerRegistry registry)
            => await RunWhitelistCommandOnActive(profiles, registry, $"whitelist remove {req.Target}", req.Target)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/whitelist/on", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunWhitelistCommand(sup, "whitelist on", null)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/whitelist/on", async (ProfilesService profiles, ServerRegistry registry)
            => await RunWhitelistCommandOnActive(profiles, registry, "whitelist on", null)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/whitelist/off", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunWhitelistCommand(sup, "whitelist off", null)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/whitelist/off", async (ProfilesService profiles, ServerRegistry registry)
            => await RunWhitelistCommandOnActive(profiles, registry, "whitelist off", null)).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/players/whitelist/reload", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await RunWhitelistCommand(sup, "whitelist reload", null)
                : Results.NotFound())
            .RequireRole(Role.Operator);
        app.MapPost("/api/players/whitelist/reload", async (ProfilesService profiles, ServerRegistry registry)
            => await RunWhitelistCommandOnActive(profiles, registry, "whitelist reload", null)).RequireRole(Role.Operator);
    }

    // Reason must stay null when Available is true — coercing a legitimately
    // null LocalFileUnavailableReason to NoServerStarted would claim nothing
    // is running for a perfectly normal local Managed server.
    private static object BannedSnapshot(ServerSupervisor? sup) => new
    {
        Available = sup is not null && sup.LocalFileUnavailableReason is null,
        Reason = sup is null ? ServerScope.NoServerStarted : sup.LocalFileUnavailableReason,
        Entries = sup is null ? [] : ReadServerJson<BannedPlayerEntry>(sup, "banned-players.json"),
    };

    private static object BannedIpsSnapshot(ServerSupervisor? sup) => new
    {
        Available = sup is not null && sup.LocalFileUnavailableReason is null,
        Reason = sup is null ? ServerScope.NoServerStarted : sup.LocalFileUnavailableReason,
        Entries = sup is null ? [] : ReadServerJson<BannedIpEntry>(sup, "banned-ips.json"),
    };

    private static object WhitelistSnapshot(ServerSupervisor? sup) => new
    {
        Available = sup is not null && sup.LocalFileUnavailableReason is null,
        Reason = sup is null ? ServerScope.NoServerStarted : sup.LocalFileUnavailableReason,
        Entries = sup is null ? [] : ReadServerJson<WhitelistEntry>(sup, "whitelist.json"),
        // white-list is the historical spelling and is still what the file uses.
        Enabled = sup is not null && ReadServerProperty(sup, "white-list") == "true",
    };

    private static Task<IResult> RunPlayerCommandOnActive(
        ProfilesService profiles, ServerRegistry registry, string verb, string target, string? reason)
        => WithActive(profiles, registry, sup => RunPlayerCommand(sup, verb, target, reason));

    private static Task<IResult> RunWhitelistCommandOnActive(
        ProfilesService profiles, ServerRegistry registry, string command, string? target)
        => WithActive(profiles, registry, sup => RunWhitelistCommand(sup, command, target));

    /// <summary>Runs an action against the active server, or reports the same "nothing started" state a real supervisor would.</summary>
    private static async Task<IResult> WithActive(
        ProfilesService profiles, ServerRegistry registry, Func<ServerSupervisor, Task<IResult>> action)
    {
        var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
        return sup is null
            ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
            : await action(sup);
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
