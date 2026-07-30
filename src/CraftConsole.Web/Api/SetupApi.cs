using CraftConsole.Core.Java;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Profiles, Java detection/downloads, server JAR downloads, and app settings.</summary>
public static class SetupApi
{
    public record ServerDownloadRequest(ServerType Type, string? Version, string Directory);
    public record JavaDownloadRequest(int Major);
    public record SetRconPasswordRequest(string Password);
    public record SettingsDto(
        bool ShowTimestamp, bool ShowDate, bool AutoScrollConsole, int MaxConsoleLines,
        string ColorInfo, string ColorWarn, string ColorError, string ColorPlayer);

    public static void MapSetupApi(this IEndpointRouteBuilder app)
    {
        // ── Profiles ──────────────────────────────────────────────────────
        app.MapGet("/api/profiles", async (ProfilesService profiles, RconSecretStore secrets, SettingsHolder settings) =>
        {
            var list = await profiles.ListAsync();
            var withFlags = new List<object>(list.Count);
            foreach (var p in list)
            {
                // ServerProfile itself never carries the password (see RconSecretStore) —
                // this flag is computed per-request so the UI can say "set" vs "unset"
                // without the value ever existing anywhere the client can read it.
                var hasPassword = p.Mode == ConnectionMode.Rcon && await secrets.HasAsync(p.Id);
                withFlags.Add(new
                {
                    p.Id, p.Name, p.Mode,
                    p.JarPath, p.WorkingDirectory, p.JavaPath, p.MinRamMb, p.MaxRamMb,
                    p.MinecraftVersion, p.JvmArguments, p.Type,
                    p.RconHost, p.RconPort,
                    HasRconPassword = hasPassword,
                });
            }
            return Results.Json(new { Profiles = withFlags, ActiveProfileId = settings.Current.ActiveProfileId }, Json.Options);
        });

        app.MapPost("/api/profiles", async (ServerProfile profile, ProfilesService profiles) =>
        {
            try { return Results.Json(await profiles.AddAsync(profile), Json.Options); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        });

        app.MapPut("/api/profiles/{id:guid}", async (Guid id, ServerProfile profile, ProfilesService profiles) =>
        {
            try { return await profiles.UpdateAsync(id, profile) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        });

        app.MapDelete("/api/profiles/{id:guid}", async (Guid id, ProfilesService profiles) =>
            await profiles.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/profiles/{id:guid}/activate", async (Guid id, ProfilesService profiles) =>
        {
            if (await profiles.GetAsync(id) is null) return Results.NotFound();
            await profiles.SetActiveAsync(id);
            return Results.NoContent();
        });

        // Write-only: sets or replaces the password, never returns it. GET
        // /api/profiles exposes only whether one is set (HasRconPassword above).
        app.MapPut("/api/profiles/{id:guid}/rcon-password",
            async (Guid id, SetRconPasswordRequest req, ProfilesService profiles, RconSecretStore secrets) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            if (profile.Mode != ConnectionMode.Rcon)
                return Results.BadRequest(new { Message = "Only RCON profiles have a password." });
            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { Message = "A password is required." });

            await secrets.SetAsync(id, req.Password);
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
        {
            // Lets the frontend show a Debian/Ubuntu install-command hint alongside the
            // download — this is the endpoint it already calls to populate that same picker.
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
            return Results.Json(new { Platform = platform, Versions = await setup.FetchJavaVersionsAsync(ct) }, Json.Options);
        });

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
