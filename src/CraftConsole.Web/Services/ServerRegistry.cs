namespace CraftConsole.Web.Services;

/// <summary>
/// Owns one <see cref="ServerSupervisor"/> per profile that has ever been
/// started. A profile that has never been started has no entry — the profile
/// list (identity: name, mode) already comes from <see cref="ProfilesService"/>,
/// so nothing here needs to exist until there is live state to hold.
///
/// This is the DI singleton now; ServerSupervisor itself is not — see its own
/// doc comment.
/// </summary>
public sealed class ServerRegistry
{
    private readonly EventBroker _broker;
    private readonly SettingsHolder _settings;
    private readonly HttpClient _http;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RconSecretStore _secrets;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, ServerSupervisor> _supervisors = [];

    /// <summary>
    /// Raised whenever a new supervisor is created — lets SchedulerService (and
    /// anything else that needs to observe every server's GameEvent) subscribe
    /// to servers that come into existence after it does, without polling.
    /// </summary>
    public event Action<ServerSupervisor>? SupervisorCreated;

    public ServerRegistry(
        EventBroker broker, SettingsHolder settings, HttpClient http,
        ILoggerFactory loggerFactory, RconSecretStore secrets)
    {
        _broker = broker;
        _settings = settings;
        _http = http;
        _loggerFactory = loggerFactory;
        _secrets = secrets;
    }

    /// <summary>Existing supervisor for this server, or a freshly created one — never null.</summary>
    public ServerSupervisor GetOrCreate(Guid serverId)
    {
        lock (_lock)
        {
            if (_supervisors.TryGetValue(serverId, out var existing)) return existing;

            var supervisor = new ServerSupervisor(
                serverId, _broker, _settings, _http, _loggerFactory.CreateLogger<ServerSupervisor>(), _secrets);
            _supervisors[serverId] = supervisor;
            SupervisorCreated?.Invoke(supervisor);
            return supervisor;
        }
    }

    /// <summary>Null when this server has never been started — no live state to report.</summary>
    public ServerSupervisor? TryGet(Guid serverId)
    {
        lock (_lock) return _supervisors.GetValueOrDefault(serverId);
    }

    /// <summary>Every server with live state — i.e. every one ever started, running or not.</summary>
    public List<ServerSupervisor> All()
    {
        lock (_lock) return [.. _supervisors.Values];
    }

    /// <summary>
    /// Stops and drops a server's supervisor — called when its profile is
    /// deleted. A no-op if it was never started, since there is nothing to
    /// dispose.
    /// </summary>
    public async Task RemoveAsync(Guid serverId)
    {
        ServerSupervisor? supervisor;
        lock (_lock)
        {
            if (!_supervisors.Remove(serverId, out supervisor)) return;
        }
        await supervisor.DisposeAsync();
    }

    /// <summary>
    /// Stops every managed server gracefully. Called on panel shutdown — a
    /// single supervisor's DisposeAsync already skips this for an RCON
    /// connection (see its own doc comment); this just does that for all of
    /// them rather than one.
    /// </summary>
    public async Task DisposeAllAsync()
    {
        List<ServerSupervisor> all;
        lock (_lock) all = [.. _supervisors.Values];

        foreach (var supervisor in all)
        {
            try { await supervisor.DisposeAsync(); }
            catch { /* best-effort graceful stop on shutdown, same as before */ }
        }
    }
}
