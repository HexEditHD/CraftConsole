using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>CurseForge search, file listing, install, remove, and API-key management for the Plugins screen's Browse tab.</summary>
public static class CurseForgeApi
{
    public record InstallRequest(int ModId, int FileId, bool IncludeDependencies);
    public record ApiKeyRequest(string ApiKey);

    public static void MapCurseForgeApi(this IEndpointRouteBuilder app)
    {
        // Search and file listing resolve the profile directly rather than
        // through ServerScope — same reasoning as ModrinthApi: pure network
        // calls, no local-file dependency, available for a profile that has
        // never been started. Install/list/remove need a resolved supervisor
        // because they touch the working directory.
        app.MapGet("/api/servers/{id:guid}/curseforge/search", async (
            Guid id, string? query, int? offset, int? limit,
            ProfilesService profiles, CurseForgeService curseforge, CancellationToken ct) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            return await SearchAsync(curseforge, profile, query, offset, limit, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/curseforge/search", async (
            string? query, int? offset, int? limit,
            ProfilesService profiles, CurseForgeService curseforge, CancellationToken ct) =>
        {
            if (await profiles.GetActiveAsync() is not { } profile)
                return Results.BadRequest(new { Message = "No server profile exists yet." });
            return await SearchAsync(curseforge, profile, query, offset, limit, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/servers/{id:guid}/curseforge/files", async (
            Guid id, int modId, ProfilesService profiles, CurseForgeService curseforge, CancellationToken ct) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            return await FilesAsync(curseforge, profile, modId, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/curseforge/files", async (
            int modId, ProfilesService profiles, CurseForgeService curseforge, CancellationToken ct) =>
        {
            if (await profiles.GetActiveAsync() is not { } profile)
                return Results.BadRequest(new { Message = "No server profile exists yet." });
            return await FilesAsync(curseforge, profile, modId, ct);
        }).RequireRole(Role.Admin);

        app.MapPost("/api/servers/{id:guid}/curseforge/install", async (
            Guid id, InstallRequest req, ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge, CancellationToken ct) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await InstallAsync(sup, req, curseforge, ct)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/curseforge/install", async (
            InstallRequest req, ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge, CancellationToken ct) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : await InstallAsync(sup, req, curseforge, ct);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/servers/{id:guid}/curseforge/installed", async (
            Guid id, ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(await curseforge.ListInstalledAsync(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/curseforge/installed", async (
            ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(sup is null ? [] : await curseforge.ListInstalledAsync(sup), Json.Options);
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/servers/{id:guid}/curseforge/{modId:int}", async (
            Guid id, int modId, ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            return await curseforge.RemoveAsync(sup, modId) ? Results.NoContent() : Results.NotFound();
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/curseforge/{modId:int}", async (
            int modId, ProfilesService profiles, ServerRegistry registry, CurseForgeService curseforge) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.BadRequest(new { Message = ServerScope.NoServerStarted });
            return await curseforge.RemoveAsync(sup, modId) ? Results.NoContent() : Results.NotFound();
        }).RequireRole(Role.Admin);

        // ── API key ──────────────────────────────────────────────────────
        // Write-only, same convention as PUT /api/profiles/{id}/rcon-password
        // — never returns the key, GET /api/settings only exposes whether one
        // is set (HasCurseForgeApiKey).
        app.MapPut("/api/settings/curseforge-key", async (ApiKeyRequest req, CurseForgeSecretStore secrets) =>
        {
            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return Results.BadRequest(new { Message = "An API key is required." });
            await secrets.SetAsync(req.ApiKey);
            return Results.NoContent();
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/settings/curseforge-key", async (CurseForgeSecretStore secrets) =>
        {
            await secrets.RemoveAsync();
            return Results.NoContent();
        }).RequireRole(Role.Admin);
    }

    private static async Task<IResult> SearchAsync(
        CurseForgeService curseforge, ServerProfile profile, string? query, int? offset, int? limit, CancellationToken ct)
    {
        try
        {
            var result = await curseforge.SearchAsync(profile, query ?? "", offset ?? 0, Math.Clamp(limit ?? 20, 1, 50), ct);
            return Results.Json(result, Json.Options);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        catch (HttpRequestException ex)
        {
            return Results.Json(new { Message = $"Could not reach CurseForge: {ex.Message}" }, Json.Options, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> FilesAsync(CurseForgeService curseforge, ServerProfile profile, int modId, CancellationToken ct)
    {
        try
        {
            return Results.Json(await curseforge.GetFilesAsync(profile, modId, ct), Json.Options);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        catch (HttpRequestException ex)
        {
            return Results.Json(new { Message = $"Could not reach CurseForge: {ex.Message}" }, Json.Options, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> InstallAsync(ServerSupervisor sup, InstallRequest req, CurseForgeService curseforge, CancellationToken ct)
    {
        try
        {
            return Results.Json(await curseforge.InstallAsync(sup, req.ModId, req.FileId, req.IncludeDependencies, ct), Json.Options);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        catch (HttpRequestException ex) { return Results.BadRequest(new { Message = $"Could not reach CurseForge: {ex.Message}" }); }
    }
}
