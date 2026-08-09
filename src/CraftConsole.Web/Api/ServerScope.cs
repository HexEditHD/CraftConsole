using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>
/// Resolves which server a request means. Two flavours: the new explicit
/// /api/servers/{id}/... routes validate the id names a real profile before
/// touching the registry; the legacy unscoped routes (kept as thin aliases so
/// the existing test suite and any external caller work unchanged while the
/// frontend migrates) resolve to whichever profile is currently active — the
/// same "the server" notion the single-server app had, repurposed as
/// "last viewed" rather than "the only one that exists". Delete the legacy
/// half, and every route that calls it, once nothing still hits the unscoped
/// paths.
/// </summary>
internal static class ServerScope
{
    public const string NoServerStarted = "No server has been started yet.";

    /// <summary>Null when id does not name an existing profile.</summary>
    public static async Task<ServerSupervisor?> ResolveAsync(Guid id, ProfilesService profiles, ServerRegistry registry)
        => await profiles.GetAsync(id) is not null ? registry.GetOrCreate(id) : null;

    /// <summary>
    /// The active profile's supervisor, creating one in its default (never
    /// started) state if none exists yet — the same shape a fresh single-server
    /// app always had one in. Null only when there is no active profile at all
    /// (no profiles exist, or the active one was deleted).
    /// </summary>
    public static async Task<ServerSupervisor?> ResolveActiveAsync(ProfilesService profiles, ServerRegistry registry)
    {
        var profile = await profiles.GetActiveAsync();
        return profile is null ? null : registry.GetOrCreate(profile.Id);
    }
}
