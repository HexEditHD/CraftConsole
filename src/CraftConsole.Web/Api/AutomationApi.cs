using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Backup jobs and scheduled tasks.</summary>
public static class AutomationApi
{
    public static void MapAutomationApi(this IEndpointRouteBuilder app)
    {
        // ── Backups ───────────────────────────────────────────────────────
        app.MapGet("/api/backups", async (BackupService backups) =>
            Results.Json(new { Jobs = await backups.SnapshotAsync() }, Json.Options));

        app.MapPost("/api/backups", async (BackupJob job, BackupService backups) =>
            Results.Json(await backups.AddAsync(job), Json.Options));

        app.MapPut("/api/backups/{id:guid}", async (Guid id, BackupJob job, BackupService backups) =>
            await backups.UpdateAsync(id, job) ? Results.NoContent() : Results.NotFound());

        app.MapDelete("/api/backups/{id:guid}", async (Guid id, BackupService backups) =>
            await backups.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/backups/{id:guid}/run", async (Guid id, BackupService backups) =>
            await backups.RunAsync(id) ? Results.Accepted() : Results.NotFound());

        // ── Scheduled tasks ───────────────────────────────────────────────
        app.MapGet("/api/tasks", (SchedulerService scheduler) =>
            Results.Json(new { Tasks = scheduler.Snapshot() }, Json.Options));

        app.MapPost("/api/tasks", async (ScheduledTask task, SchedulerService scheduler) =>
            Results.Json(await scheduler.AddAsync(task), Json.Options));

        app.MapPut("/api/tasks/{id:guid}", async (Guid id, ScheduledTask task, SchedulerService scheduler) =>
            await scheduler.UpdateAsync(id, task) ? Results.NoContent() : Results.NotFound());

        app.MapDelete("/api/tasks/{id:guid}", async (Guid id, SchedulerService scheduler) =>
            await scheduler.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/tasks/{id:guid}/run", async (Guid id, SchedulerService scheduler) =>
            await scheduler.RunNowAsync(id) ? Results.Accepted() : Results.NotFound());
    }
}
