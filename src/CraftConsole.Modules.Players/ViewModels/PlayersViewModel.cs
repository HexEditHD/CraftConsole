using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Players;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Players.ViewModels;

public partial class PlayersViewModel : ObservableObject
{
    private IMinecraftServer? _server;
    private IDisposable? _subscription;
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, string> _geoCache = new();

    // ── Online players ───────────────────────────────────────────────────
    public ObservableCollection<Player> Players { get; } = [];
    [ObservableProperty] private Player? _selectedPlayer;

    // ── Banned players (from banned-players.json) ────────────────────────
    public ObservableCollection<BannedPlayerEntry> BannedPlayers { get; } = [];
    [ObservableProperty] private BannedPlayerEntry? _selectedBannedPlayer;

    // ── Banned IPs (from banned-ips.json) ────────────────────────────────
    public ObservableCollection<BannedIpEntry> BannedIps { get; } = [];
    [ObservableProperty] private BannedIpEntry? _selectedBannedIp;

    /// <summary>Wired by PlayersView.axaml.cs to show a reason input dialog.</summary>
    public Func<string, Task<string?>>? ShowReasonDialogAsync { get; set; }

    public void Attach(IMinecraftServer server)
    {
        _subscription?.Dispose();
        _server = server;
        Players.Clear();

        _subscription = server.ConsoleOutput.Subscribe(entry =>
        {
            var evt = ServerEventParser.TryParse(entry);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                switch (evt)
                {
                    case PlayerJoinedEvent j:
                        var existing = Players.FirstOrDefault(p => p.Username == j.Player.Username);
                        if (existing is not null)
                        {
                            // "logged in" event may arrive after "joined the game" — patch IP if now known
                            if (existing.IpAddress is null && j.Player.IpAddress is not null)
                            {
                                existing.IpAddress = j.Player.IpAddress;
                                _ = ResolveLocationAsync(existing);
                            }
                            break;
                        }
                        Players.Add(j.Player);
                        _ = ResolveLocationAsync(j.Player);
                        break;

                    case PlayerLeftEvent l:
                        var p = Players.FirstOrDefault(x => x.Username == l.Username);
                        if (p is not null)
                        {
                            p.LastSeen = DateTimeOffset.UtcNow;
                            Players.Remove(p);
                        }
                        break;
                }
            });
        });

        _ = LoadBannedListsAsync(server.Profile.WorkingDirectory);
    }

    private async Task LoadBannedListsAsync(string workingDir)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        await LoadJsonListAsync<BannedPlayerEntry>(
            Path.Combine(workingDir, "banned-players.json"),
            BannedPlayers, options);

        await LoadJsonListAsync<BannedIpEntry>(
            Path.Combine(workingDir, "banned-ips.json"),
            BannedIps, options);
    }

    private static async Task LoadJsonListAsync<T>(
        string path,
        ObservableCollection<T> target,
        JsonSerializerOptions options)
    {
        try
        {
            if (!File.Exists(path)) return;
            await using var fs = File.OpenRead(path);
            var items = await JsonSerializer.DeserializeAsync<List<T>>(fs, options);
            if (items is null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                target.Clear();
                foreach (var item in items) target.Add(item);
            });
        }
        catch { /* file may be locked or malformed */ }
    }

    private async Task ResolveLocationAsync(Player player)
    {
        if (player.IpAddress is null) return;
        if (_geoCache.TryGetValue(player.IpAddress, out var cached))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => player.Location = cached);
            return;
        }

        try
        {
            var url  = $"https://ipinfo.io/{player.IpAddress}/json";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            var city    = root.TryGetProperty("city",    out var c) ? c.GetString() : null;
            var region  = root.TryGetProperty("region",  out var r) ? r.GetString() : null;
            var country = root.TryGetProperty("country", out var n) ? n.GetString() : null;
            var parts   = new[] { city, region, country }.Where(s => !string.IsNullOrEmpty(s));
            _geoCache[player.IpAddress] = string.Join(", ", parts) is { Length: > 0 } loc ? loc : "—";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => player.Location = _geoCache[player.IpAddress]);
        }
        catch { /* ignore network errors */ }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelectedPlayer))]
    private async Task KickPlayerAsync()
    {
        if (_server is null || SelectedPlayer is null) return;
        var reason = await PromptReason("Kick reason (optional):");
        if (reason is null) return;
        var cmd = string.IsNullOrWhiteSpace(reason)
            ? $"kick {SelectedPlayer.Username}"
            : $"kick {SelectedPlayer.Username} {reason}";
        await _server.SendCommandAsync(cmd);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPlayer))]
    private async Task BanPlayerAsync()
    {
        if (_server is null || SelectedPlayer is null) return;
        var reason = await PromptReason("Ban reason (optional):");
        if (reason is null) return;
        var cmd = string.IsNullOrWhiteSpace(reason)
            ? $"ban {SelectedPlayer.Username}"
            : $"ban {SelectedPlayer.Username} {reason}";
        await _server.SendCommandAsync(cmd);
        Players.Remove(SelectedPlayer);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPlayer))]
    private async Task BanPlayerIpAsync()
    {
        if (_server is null || SelectedPlayer is null) return;
        var reason = await PromptReason("Ban-IP reason (optional):");
        if (reason is null) return;
        var target = SelectedPlayer.IpAddress ?? SelectedPlayer.Username;
        var cmd = string.IsNullOrWhiteSpace(reason)
            ? $"ban-ip {target}"
            : $"ban-ip {target} {reason}";
        await _server.SendCommandAsync(cmd);
        Players.Remove(SelectedPlayer);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBannedPlayer))]
    private async Task PardonPlayerAsync()
    {
        if (_server is null || SelectedBannedPlayer is null) return;
        await _server.SendCommandAsync($"pardon {SelectedBannedPlayer.Name}");
        BannedPlayers.Remove(SelectedBannedPlayer);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBannedIp))]
    private async Task PardonIpAsync()
    {
        if (_server is null || SelectedBannedIp is null) return;
        await _server.SendCommandAsync($"pardon-ip {SelectedBannedIp.Ip}");
        BannedIps.Remove(SelectedBannedIp);
    }

    private async Task<string?> PromptReason(string prompt)
    {
        if (ShowReasonDialogAsync is not null)
            return await ShowReasonDialogAsync(prompt);
        return string.Empty;
    }

    private bool HasSelectedPlayer()      => SelectedPlayer is not null;
    private bool HasSelectedBannedPlayer() => SelectedBannedPlayer is not null;
    private bool HasSelectedBannedIp()     => SelectedBannedIp is not null;

    partial void OnSelectedPlayerChanged(Player? value)
    {
        KickPlayerCommand.NotifyCanExecuteChanged();
        BanPlayerCommand.NotifyCanExecuteChanged();
        BanPlayerIpCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBannedPlayerChanged(BannedPlayerEntry? value)
        => PardonPlayerCommand.NotifyCanExecuteChanged();

    partial void OnSelectedBannedIpChanged(BannedIpEntry? value)
        => PardonIpCommand.NotifyCanExecuteChanged();
}
