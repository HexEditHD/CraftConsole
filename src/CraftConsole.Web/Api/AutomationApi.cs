using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Backup jobs and scheduled tasks.</summary>
public static class AutomationApi
{
    public record RestoreRequest(string Archive, string TargetDirectory);
    public record SetEnabledRequest(bool Enabled);

    public static void MapAutomationApi(this IEndpointRouteBuilder app)
    {
        // ── Backups ───────────────────────────────────────────────────────
        app.MapGet("/api/backups", async (BackupService backups) =>
            Results.Json(new { Jobs = await backups.SnapshotAsync() }, Json.Options)).RequireRole(Role.Operator);

        app.MapPost("/api/backups", async (BackupJob job, BackupService backups) =>
            Results.Json(await backups.AddAsync(job), Json.Options)).RequireRole(Role.Admin);

        app.MapPut("/api/backups/{id:guid}", async (Guid id, BackupJob job, BackupService backups) =>
            await backups.UpdateAsync(id, job) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapDelete("/api/backups/{id:guid}", async (Guid id, BackupService backups) =>
            await backups.DeleteAsync(id) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/backups/{id:guid}/run", async (Guid id, BackupService backups) =>
        {
            try
            {
                return await backups.RunAsync(id) ? Results.Accepted() : Results.NotFound();
            }
            catch (BackupService.BackupDisabledException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        app.MapGet("/api/backups/{id:guid}/archives", async (Guid id, BackupService backups) =>
            await backups.ListArchivesAsync(id) is { } archives
                ? Results.Json(new { Archives = archives }, Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        // Admin-only: TargetDirectory is client-supplied and only Path.GetFullPath'd,
        // so this endpoint is effectively an arbitrary host write for whoever can call it.
        //
        // A backup job's source/destination paths are plain directories, not tied
        // to any one server profile (see BackupJob), so there is no server id to
        // scope this to — the guard below checks whichever server is currently
        // active, same as pre-multi-server behaviour. It is an imperfect proxy in
        // a world with several servers running at once (restoring could still
        // collide with a *different* running server's files), but making this
        // properly server-aware is a bigger change than this endpoint's current
        // contract, and out of scope here.
        app.MapPost("/api/backups/{id:guid}/restore",
            async (Guid id, RestoreRequest req, BackupService backups, ProfilesService profiles, ServerRegistry registry) =>
        {
            // Restoring over a live world would fight the server for file handles
            // and be silently overwritten by the next autosave.
            var active = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (active is not null && active.Status is not (ServerStatus.Stopped or ServerStatus.Crashed))
                return Results.BadRequest(new
                {
                    Message = "Stop the server before restoring a backup — restoring over a running world would corrupt it."
                });

            try
            {
                await backups.RestoreAsync(id, req.Archive, req.TargetDirectory);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Admin);

        // ── Scheduled tasks ───────────────────────────────────────────────
        app.MapGet("/api/tasks", (SchedulerService scheduler) =>
            Results.Json(new { Tasks = scheduler.Snapshot() }, Json.Options)).RequireRole(Role.Admin);

        app.MapPost("/api/tasks", async (ScheduledTask task, SchedulerService scheduler) =>
            Results.Json(await scheduler.AddAsync(task), Json.Options)).RequireRole(Role.Admin);

        app.MapPut("/api/tasks/{id:guid}", async (Guid id, ScheduledTask task, SchedulerService scheduler) =>
            await scheduler.UpdateAsync(id, task) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/tasks/{id:guid}/enabled", async (Guid id, SetEnabledRequest req, SchedulerService scheduler) =>
            await scheduler.SetEnabledAsync(id, req.Enabled) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapDelete("/api/tasks/{id:guid}", async (Guid id, SchedulerService scheduler) =>
            await scheduler.DeleteAsync(id) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/tasks/{id:guid}/run", async (Guid id, SchedulerService scheduler) =>
            await scheduler.RunNowAsync(id) ? Results.Accepted() : Results.NotFound())
            .RequireRole(Role.Admin);
    }
}
