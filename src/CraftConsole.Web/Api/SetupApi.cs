using CraftConsole.Core.Java;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Profiles, Java detection/downloads, server JAR downloads, and app settings.</summary>
public static class SetupApi
{
    public record ServerDownloadRequest(ServerType Type, string? Version, string Directory);
    public record JavaDownloadRequest(int Major);
    public record SettingsDto(
        bool ShowTimestamp, bool ShowDate, bool AutoScrollConsole, int MaxConsoleLines,
        string ColorInfo, string ColorWarn, string ColorError, string ColorPlayer);

    public static void MapSetupApi(this IEndpointRouteBuilder app)
    {
        // ── Profiles ──────────────────────────────────────────────────────
        app.MapGet("/api/profiles", async (ProfilesService profiles, SettingsHolder settings) =>
            Results.Json(new
            {
                Profiles = await profiles.ListAsync(),
                ActiveProfileId = settings.Current.ActiveProfileId,
            }, Json.Options));

        app.MapPost("/api/profiles", async (ServerProfile profile, ProfilesService profiles) =>
            Results.Json(await profiles.AddAsync(profile), Json.Options));

        app.MapPut("/api/profiles/{id:guid}", async (Guid id, ServerProfile profile, ProfilesService profiles) =>
            await profiles.UpdateAsync(id, profile) ? Results.NoContent() : Results.NotFound());

        app.MapDelete("/api/profiles/{id:guid}", async (Guid id, ProfilesService profiles) =>
            await profiles.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/profiles/{id:guid}/activate", async (Guid id, ProfilesService profiles) =>
        {
            if (await profiles.GetAsync(id) is null) return Results.NotFound();
            await profiles.SetActiveAsync(id);
            return Results.NoContent();
        });

        // ── Java ──────────────────────────────────────────────────────────
        app.MapGet("/api/setup/java/detect", async () =>
        {
            var found = await JavaInstallationDetector.DetectAsync();
            return Results.Json(found
                .OrderByDescending(j => j.MajorVersion)
                .Select(j => new { j.ExecutablePath, j.DisplayVersion, j.MajorVersion, j.Label }),
                Json.Options);
        });

        app.MapGet("/api/setup/java/versions", async (SetupService setup, CancellationToken ct) =>
            Results.Json(await setup.FetchJavaVersionsAsync(ct), Json.Options));

        app.MapPost("/api/setup/java/download", (JavaDownloadRequest req, SetupService setup) =>
            setup.StartJavaDownload(req.Major)
                ? Results.Accepted()
                : Results.Conflict(new { Message = "A Java download is already in progress." }));

        // ── Server JARs ───────────────────────────────────────────────────
        app.MapGet("/api/setup/server/types", () =>
            Results.Json(SetupService.AllServerTypes, Json.Options));

        app.MapGet("/api/setup/server/versions", async (ServerType type, SetupService setup, CancellationToken ct) =>
        {
            try { return Results.Json(await setup.FetchServerVersionsAsync(type, ct), Json.Options); }
            catch { return Results.Json(new List<string>(), Json.Options); }
        });

        app.MapPost("/api/setup/server/download", (ServerDownloadRequest req, SetupService setup) =>
        {
            if (string.IsNullOrWhiteSpace(req.Directory))
                return Results.BadRequest(new { Message = "A destination directory is required." });

            return setup.StartServerDownload(req.Type, req.Version, req.Directory)
                ? Results.Accepted()
                : Results.Conflict(new { Message = "A server download is already in progress." });
        });

        app.MapPost("/api/setup/cancel/{kind}", (string kind, SetupService setup) =>
        {
            setup.Cancel(kind);
            return Results.NoContent();
        });

        // ── Settings ──────────────────────────────────────────────────────
        app.MapGet("/api/settings", (SettingsHolder settings) =>
            Results.Json(settings.Current, Json.Options));

        app.MapPut("/api/settings", async (SettingsDto dto, SettingsHolder settings) =>
        {
            await settings.UpdateAsync(s =>
            {
                s.ShowTimestamp = dto.ShowTimestamp;
                s.ShowDate = dto.ShowDate;
                s.AutoScrollConsole = dto.AutoScrollConsole;
                s.MaxConsoleLines = Math.Clamp(dto.MaxConsoleLines, 100, 20000);
                s.ColorInfo = dto.ColorInfo;
                s.ColorWarn = dto.ColorWarn;
                s.ColorError = dto.ColorError;
                s.ColorPlayer = dto.ColorPlayer;
            });
            return Results.Json(settings.Current, Json.Options);
        });
    }
}
