using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Modrinth search, version listing, install, and remove for the Plugins screen's Browse tab.</summary>
public static class ModrinthApi
{
    public record InstallRequest(string VersionId, bool IncludeDependencies);

    public static void MapModrinthApi(this IEndpointRouteBuilder app)
    {
        // Search and version listing resolve the profile directly rather than
        // through ServerScope — they're pure network calls with no local-file
        // dependency, so a profile that has never been started can still be
        // browsed. Install/list/remove need a resolved supervisor because they
        // write into (or read from) the working directory, the same gate
        // WorkspaceApi's plugin/file routes already apply.
        app.MapGet("/api/servers/{id:guid}/modrinth/search", async (
            Guid id, string? query, int? offset, int? limit,
            ProfilesService profiles, ModrinthService modrinth, CancellationToken ct) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            return await SearchAsync(modrinth, profile, query, offset, limit, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/modrinth/search", async (
            string? query, int? offset, int? limit,
            ProfilesService profiles, ModrinthService modrinth, CancellationToken ct) =>
        {
            if (await profiles.GetActiveAsync() is not { } profile)
                return Results.BadRequest(new { Message = "No server profile exists yet." });
            return await SearchAsync(modrinth, profile, query, offset, limit, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/servers/{id:guid}/modrinth/versions", async (
            Guid id, string projectId, ProfilesService profiles, ModrinthService modrinth, CancellationToken ct) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            return await VersionsAsync(modrinth, profile, projectId, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/modrinth/versions", async (
            string projectId, ProfilesService profiles, ModrinthService modrinth, CancellationToken ct) =>
        {
            if (await profiles.GetActiveAsync() is not { } profile)
                return Results.BadRequest(new { Message = "No server profile exists yet." });
            return await VersionsAsync(modrinth, profile, projectId, ct);
        }).RequireRole(Role.Admin);

        app.MapPost("/api/servers/{id:guid}/modrinth/install", async (
            Guid id, InstallRequest req, ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth, CancellationToken ct) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await InstallAsync(sup, req, modrinth, ct)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/modrinth/install", async (
            InstallRequest req, ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth, CancellationToken ct) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : await InstallAsync(sup, req, modrinth, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/servers/{id:guid}/modrinth/installed", async (
            Guid id, ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(await modrinth.ListInstalledAsync(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/modrinth/installed", async (
            ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(sup is null ? [] : await modrinth.ListInstalledAsync(sup), Json.Options);
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/servers/{id:guid}/modrinth/{projectId}", async (
            Guid id, string projectId, ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            return await modrinth.RemoveAsync(sup, projectId) ? Results.NoContent() : Results.NotFound();
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/modrinth/{projectId}", async (
            string projectId, ProfilesService profiles, ServerRegistry registry, ModrinthService modrinth) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.BadRequest(new { Message = ServerScope.NoServerStarted });
            return await modrinth.RemoveAsync(sup, projectId) ? Results.NoContent() : Results.NotFound();
        }).RequireRole(Role.Admin);
    }

    private static async Task<IResult> InstallAsync(ServerSupervisor sup, InstallRequest req, ModrinthService modrinth, CancellationToken ct)
    {
        try
        {
            return Results.Json(await modrinth.InstallAsync(sup, req.VersionId, req.IncludeDependencies, ct), Json.Options);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        catch (HttpRequestException ex) { return Results.BadRequest(new { Message = $"Could not reach Modrinth: {ex.Message}" }); }
    }

    private static async Task<IResult> SearchAsync(
        ModrinthService modrinth, ServerProfile profile, string? query, int? offset, int? limit, CancellationToken ct)
    {
        try
        {
            var result = await modrinth.SearchAsync(profile, query ?? "", offset ?? 0, Math.Clamp(limit ?? 20, 1, 50), ct);
            return Results.Json(result, Json.Options);
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(new { Message = $"Could not reach Modrinth: {ex.Message}" }, Json.Options, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> VersionsAsync(ModrinthService modrinth, ServerProfile profile, string projectId, CancellationToken ct)
    {
        try
        {
            return Results.Json(await modrinth.GetVersionsAsync(profile, projectId, ct), Json.Options);
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(new { Message = $"Could not reach Modrinth: {ex.Message}" }, Json.Options, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
