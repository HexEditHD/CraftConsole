using System.Text.Json;
using System.Text.RegularExpressions;
using CraftConsole.Core.Models;
using CraftConsole.Core.Players;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Web.Services;

/// <summary>
/// Owns the running Minecraft server process and every piece of live state derived
/// from its console stream: the console ring buffer, online players, detected
/// issues, server version, and EULA state. All changes are fanned out to SSE
/// clients through the <see cref="EventBroker"/>.
/// </summary>
public sealed partial class ServerSupervisor : IAsyncDisposable
{
    private readonly EventBroker _broker;
    private readonly SettingsHolder _settings;
    private readonly HttpClient _http;
    private readonly ILogger<ServerSupervisor> _log;
    private readonly RconSecretStore _secrets;

    private readonly object _lock = new();

    // Serialises the whole start path. The status guard and the _server assignment
    // used to sit outside any lock, so two concurrent POSTs to /api/server/start
    // could both pass the guard and launch a second JVM, orphaning the first.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private readonly List<ConsoleEntry> _console = [];
    private readonly List<Player> _players = [];
    private readonly List<IssueEntry> _issues = [];
    private readonly Dictionary<string, string> _geoCache = new();
    private int _nextIssueId = 1;

    private IMinecraftServer? _server;
    private IDisposable? _consoleSub;
    private IDisposable? _statusSub;

    private string _serverVersion = "";
    private int _maxPlayers = 20;
    private DateTimeOffset? _startedAt;
    private bool _eulaRequired;

    /// <summary>Raised for every parsed game event; consumed by the scheduler.</summary>
    public event Action<ServerEvent>? GameEvent;

    public ServerProfile? ActiveProfile { get; private set; }
    public ServerStatus Status => _server?.Status ?? ServerStatus.Stopped;
    public int? ProcessId => _server?.ProcessId;
    public DateTimeOffset? StartedAt => Status == ServerStatus.Running ? _startedAt : null;

    // Derived from the active profile rather than _server, so it is available
    // before a connection exists too — the UI needs to know a not-yet-started
    // RCON profile can't be restarted just as much as a connected one.
    public ServerCapabilities Capabilities =>
        ActiveProfile?.Mode == ConnectionMode.Rcon ? ServerCapabilities.Rcon : ServerCapabilities.Managed;

    [GeneratedRegex(@"Starting minecraft server version (?<ver>[\d.]+)")]
    private static partial Regex VersionPattern();

    public ServerSupervisor(
        EventBroker broker, SettingsHolder settings, HttpClient http, ILogger<ServerSupervisor> log,
        RconSecretStore secrets)
    {
        _broker = broker;
        _settings = settings;
        _http = http;
        _log = log;
        _secrets = secrets;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public async Task StartAsync(ServerProfile profile, CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            if (Status is ServerStatus.Running or ServerStatus.Starting)
                throw new InvalidOperationException("The server is already running.");

            await DisposeServerAsync();

            lock (_lock)
            {
                _players.Clear();
                _eulaRequired = false;
                _serverVersion = "";
            }

            // An already-running remote server has necessarily already accepted its
            // EULA and has its own real player cap; neither applies to a profile
            // with no local working directory to read server.properties/eula.txt from.
            _maxPlayers = profile.Mode == ConnectionMode.Managed ? ReadMaxPlayers(profile.WorkingDirectory) : 20;
            ActiveProfile = profile;

            if (profile.Mode == ConnectionMode.Managed && !IsEulaAccepted(profile.WorkingDirectory))
                lock (_lock) _eulaRequired = true;

            var server = await CreateServerAsync(profile);
            _consoleSub = server.ConsoleOutput.Subscribe(OnConsoleEntry);
            _statusSub = server.StatusChanged.Subscribe(OnStatusChanged);
            _server = server;

            AppendEntry(new ConsoleEntry(
                DateTimeOffset.Now,
                Raw: $"— starting \"{profile.Name}\" —",
                Message: $"— starting \"{profile.Name}\" —",
                Level: ConsoleEntryLevel.Input));

            try
            {
                await server.StartAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start server process");
                await DisposeServerAsync();
                AppendEntry(new ConsoleEntry(
                    DateTimeOffset.Now,
                    Raw: $"Failed to start: {ex.Message}",
                    Message: $"Failed to start: {ex.Message}",
                    Level: ConsoleEntryLevel.Error));
                PublishStatus();
                throw new InvalidOperationException($"Failed to start the server: {ex.Message}", ex);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_server is null) return;
        await _server.StopAsync(ct);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        var profile = ActiveProfile ?? throw new InvalidOperationException("No server has been started yet.");

        if (!Capabilities.CanRestart)
            throw new InvalidOperationException(
                "This server is connected over RCON and can't be restarted from here — " +
                "the panel doesn't own the process, only the server itself can bring it back.");

        await StopAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await StartAsync(profile, ct);
    }

    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var cmd = command.Trim();
        if (cmd.Length == 0) return;

        AppendEntry(new ConsoleEntry(
            DateTimeOffset.Now, Raw: $"> {cmd}", Message: $"> {cmd}", Level: ConsoleEntryLevel.Input));

        if (_server is null || Status is not (ServerStatus.Running or ServerStatus.Starting))
        {
            AppendEntry(new ConsoleEntry(
                DateTimeOffset.Now, Raw: "No server running.", Message: "No server running.",
                Level: ConsoleEntryLevel.Error));
            return;
        }

        var reply = await _server.SendCommandAsync(cmd.StartsWith('/') ? cmd[1..] : cmd, ct);

        // A managed process's reply arrives later through ConsoleOutput and lands
        // via OnConsoleEntry there — only append here when the transport (RCON)
        // handed the reply back synchronously, or it would show up twice.
        if (!string.IsNullOrWhiteSpace(reply))
        {
            foreach (var line in reply.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                OnConsoleEntry(new ConsoleEntry(DateTimeOffset.Now, Raw: line, Message: line, Level: ConsoleEntryLevel.Info));
        }
    }

    public async Task AcceptEulaAsync(CancellationToken ct = default)
    {
        var profile = ActiveProfile ?? throw new InvalidOperationException("No server has been started yet.");
        if (profile.Mode != ConnectionMode.Managed)
            throw new InvalidOperationException("Only a managed server has a local eula.txt to accept.");

        var eulaPath = Path.Combine(profile.WorkingDirectory, "eula.txt");

        try
        {
            // Rewrite only the eula= line so Mojang's boilerplate and the EULA link
            // survive; the previous version replaced the whole file.
            string[] lines = File.Exists(eulaPath)
                ? await File.ReadAllLinesAsync(eulaPath, ct)
                : [
                    "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).",
                  ];

            var rewritten = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("eula=", StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = "eula=true";
                rewritten = true;
                break;
            }

            if (!rewritten)
                lines = [.. lines, $"#Accepted via CraftConsole {DateTimeOffset.Now:u}", "eula=true"];

            await File.WriteAllLinesAsync(eulaPath, lines, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Previously this propagated as an unhandled 500.
            _log.LogError(ex, "Could not write eula.txt at {Path}", eulaPath);
            throw new InvalidOperationException(
                $"Could not write eula.txt in \"{profile.WorkingDirectory}\": {ex.Message}", ex);
        }

        lock (_lock) _eulaRequired = false;
        PublishStatus();
    }

    /// <summary>Reads eula.txt directly. Absent or eula=false both mean "not accepted".</summary>
    private static bool IsEulaAccepted(string workingDirectory)
    {
        try
        {
            var path = Path.Combine(workingDirectory, "eula.txt");
            if (!File.Exists(path)) return false;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("eula=", StringComparison.OrdinalIgnoreCase)) continue;
                return trimmed["eula=".Length..].Trim()
                    .Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* unreadable — treat as not accepted */ }

        return false;
    }

    // ── Snapshots for REST endpoints ──────────────────────────────────────

    public object StatusSnapshot()
    {
        lock (_lock)
        {
            return new
            {
                Status,
                Version = _serverVersion,
                StartedAt,
                UptimeSeconds = StartedAt is { } s ? (long)(DateTimeOffset.UtcNow - s).TotalSeconds : 0,
                PlayerCount = _players.Count,
                // RCON learns the real cap from polling `list`; before the first
                // successful poll (or for a managed server, always) this falls back
                // to the file-derived value set in StartAsync.
                MaxPlayers = _server?.MaxPlayers ?? _maxPlayers,
                EulaRequired = _eulaRequired,
                Profile = ActiveProfile,
            };
        }
    }

    public List<ConsoleEntry> ConsoleSnapshot()
    {
        lock (_lock) return [.. _console];
    }

    public void ClearConsole()
    {
        lock (_lock) _console.Clear();
        _broker.Publish("console-cleared", new { });
    }

    public List<object> PlayersSnapshot()
    {
        lock (_lock) return [.. _players.Select(PlayerDto)];
    }

    public List<IssueEntry> IssuesSnapshot()
    {
        lock (_lock) return [.. _issues];
    }

    public void ClearIssues()
    {
        lock (_lock)
        {
            _issues.Clear();
            _nextIssueId = 1;
        }
        _broker.Publish("issues-cleared", new { });
    }

    /// <summary>Development helper: push a raw line through the same pipeline as real output.</summary>
    public void SimulateOutput(string rawLine) => OnConsoleEntry(ConsoleOutputParser.Parse(rawLine));

    // ── Console pipeline ──────────────────────────────────────────────────

    private void OnConsoleEntry(ConsoleEntry entry)
    {
        AppendEntry(entry);

        if (VersionPattern().Match(entry.Message) is { Success: true } vm)
        {
            lock (_lock) _serverVersion = vm.Groups["ver"].Value;
            PublishStatus();
        }

        // Only trust the server's own output, never a player's. This matched any line,
        // so "<Steve> we should agree to the EULA" in chat raised the banner — and the
        // dev simulate endpoint could too. Chat and panel-echoed input are excluded,
        // and the claim is corroborated against eula.txt on disk.
        if (entry.Level is not ConsoleEntryLevel.Input
            && ServerEventParser.TryParse(entry) is not PlayerChatEvent
            && entry.Message.Contains("agree to the EULA", StringComparison.OrdinalIgnoreCase)
            && ActiveProfile is { } eulaProfile
            && !IsEulaAccepted(eulaProfile.WorkingDirectory))
        {
            lock (_lock) _eulaRequired = true;
            PublishStatus();
        }

        if (ClassifyIssue(entry) is { } issueType)
        {
            IssueEntry issue;
            lock (_lock)
            {
                issue = new IssueEntry
                {
                    Id = _nextIssueId++,
                    Type = issueType,
                    Timestamp = entry.Timestamp,
                    Message = entry.Message,
                };
                _issues.Add(issue);
                if (_issues.Count > 500) _issues.RemoveAt(0);
            }
            _broker.Publish("issue", issue);
        }

        if (ServerEventParser.TryParse(entry) is { } evt)
        {
            HandleGameEvent(evt);
            GameEvent?.Invoke(evt);
        }
    }

    private void AppendEntry(ConsoleEntry entry)
    {
        lock (_lock)
        {
            _console.Add(entry);
            var max = Math.Max(100, _settings.Current.MaxConsoleLines);
            if (_console.Count > max)
                _console.RemoveRange(0, _console.Count - max);
        }
        _broker.Publish("console", entry);
    }

    private void HandleGameEvent(ServerEvent evt)
    {
        switch (evt)
        {
            case PlayerJoinedEvent joined:
                Player? geoTarget = null;
                lock (_lock)
                {
                    var existing = _players.FirstOrDefault(p => p.Username == joined.Player.Username);
                    if (existing is not null)
                    {
                        // "logged in" may arrive after "joined the game" — patch IP if now known
                        if (existing.IpAddress is null && joined.Player.IpAddress is not null)
                        {
                            existing.IpAddress = joined.Player.IpAddress;
                            geoTarget = existing;
                        }
                    }
                    else
                    {
                        _players.Add(joined.Player);
                        geoTarget = joined.Player;
                    }
                }
                if (geoTarget is not null) _ = ResolveLocationAsync(geoTarget);
                PublishPlayers();
                break;

            case PlayerLeftEvent left:
                lock (_lock)
                {
                    var player = _players.FirstOrDefault(p => p.Username == left.Username);
                    if (player is not null)
                    {
                        player.LastSeen = DateTimeOffset.UtcNow;
                        _players.Remove(player);
                    }
                }
                PublishPlayers();
                break;
        }
    }

    private void OnStatusChanged(ServerStatus status)
    {
        if (status == ServerStatus.Running)
            _startedAt = DateTimeOffset.UtcNow;

        if (status is ServerStatus.Stopped or ServerStatus.Crashed)
        {
            lock (_lock) _players.Clear();
            PublishPlayers();
        }

        PublishStatus();
    }

    private void PublishStatus() => _broker.Publish("status", StatusSnapshot());
    private void PublishPlayers() => _broker.Publish("players", new { Players = PlayersSnapshot() });

    private object PlayerDto(Player p) => new
    {
        p.Username,
        p.IpAddress,
        p.JoinedAt,
        p.Location,
        ColorHex = UsernameColor.GetHex(p.Username),
    };

    private static IssueType? ClassifyIssue(ConsoleEntry entry) => entry.Level switch
    {
        ConsoleEntryLevel.Warn => IssueType.Warning,
        ConsoleEntryLevel.Error => IssueType.Severe,
        ConsoleEntryLevel.Info when entry.Message.Contains("Can't keep up") => IssueType.Warning,
        ConsoleEntryLevel.Info when entry.Message.Contains("overloaded") => IssueType.Warning,
        ConsoleEntryLevel.Info when entry.Message.Contains("Exception") => IssueType.Severe,
        _ => null,
    };

    // ── Geo lookup (ipinfo.io, best-effort) ───────────────────────────────

    private async Task ResolveLocationAsync(Player player)
    {
        var ip = player.IpAddress;
        if (ip is null) return;

        if (IsPrivateAddress(ip))
        {
            player.Location = "Local network";
            PublishPlayers();
            return;
        }

        lock (_lock)
        {
            if (_geoCache.TryGetValue(ip, out var cached))
            {
                player.Location = cached;
                return;
            }
        }

        try
        {
            var json = await _http.GetStringAsync($"https://ipinfo.io/{ip}/json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
            var region = root.TryGetProperty("region", out var r) ? r.GetString() : null;
            var country = root.TryGetProperty("country", out var n) ? n.GetString() : null;
            var parts = new[] { city, region, country }.Where(s => !string.IsNullOrEmpty(s));
            var location = string.Join(", ", parts) is { Length: > 0 } loc ? loc : "—";

            lock (_lock) _geoCache[ip] = location;
            player.Location = location;
            PublishPlayers();
        }
        catch { /* network unavailable — location stays unknown */ }
    }

    private static bool IsPrivateAddress(string ip)
        => ip is "127.0.0.1" or "::1" or "0:0:0:0:0:0:0:1"
           || ip.StartsWith("192.168.") || ip.StartsWith("10.")
           || ip.StartsWith("172.16.") || ip.StartsWith("172.17.")
           || ip.StartsWith("172.18.") || ip.StartsWith("172.19.")
           || ip.StartsWith("172.2") || ip.StartsWith("172.30.") || ip.StartsWith("172.31.")
           || ip.StartsWith("fe80:") || ip.StartsWith("fd");

    // ── Server construction ─────────────────────────────────────────────────

    private async Task<IMinecraftServer> CreateServerAsync(ServerProfile profile)
    {
        switch (profile.Mode)
        {
            case ConnectionMode.Managed:
                return new ServerProcessManager(profile);

            case ConnectionMode.Rcon:
                var password = await _secrets.TryGetAsync(profile.Id)
                    ?? throw new InvalidOperationException(
                        "No RCON password is set for this profile — set one in the profile editor first.");
                return new RconMinecraftServer(profile, password);

            default:
                throw new NotSupportedException($"Unknown connection mode: {profile.Mode}");
        }
    }

    // ── server.properties helpers ─────────────────────────────────────────

    private static int ReadMaxPlayers(string workingDirectory)
    {
        try
        {
            var path = Path.Combine(workingDirectory, "server.properties");
            if (!File.Exists(path)) return 20;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("max-players=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["max-players=".Length..].Trim(), out var max) && max > 0)
                    return max;
            }
        }
        catch { /* unreadable — keep default */ }
        return 20;
    }

    // ── Disposal ──────────────────────────────────────────────────────────

    private async Task DisposeServerAsync()
    {
        _consoleSub?.Dispose();
        _statusSub?.Dispose();
        _consoleSub = null;
        _statusSub = null;

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Only a managed process is the panel's to shut down. Auto-stopping here
        // would be wrong for RCON — it would shut down someone else's running
        // server as a side effect of the panel itself closing, or of the operator
        // simply switching to a different profile (StartAsync routes through
        // DisposeServerAsync below for that too).
        if (ActiveProfile?.Mode == ConnectionMode.Managed
            && Status is ServerStatus.Running or ServerStatus.Starting)
        {
            try { await StopAsync(); }
            catch { /* best-effort graceful stop on shutdown */ }
        }
        await DisposeServerAsync();
    }
}
