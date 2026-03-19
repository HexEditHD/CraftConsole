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

    public ObservableCollection<Player> Players { get; } = [];
    public ObservableCollection<Player> BannedPlayers { get; } = [];

    [ObservableProperty] private Player? _selectedPlayer;
    [ObservableProperty] private Player? _selectedBannedPlayer;

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
                        if (Players.Any(p => p.Username == j.Player.Username)) break;
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
            var url  = $"http://ip-api.com/json/{player.IpAddress}?fields=country,city";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            var city    = root.TryGetProperty("city",    out var c) ? c.GetString() : null;
            var country = root.TryGetProperty("country", out var n) ? n.GetString() : null;
            var location = string.IsNullOrEmpty(city) ? country : $"{city}, {country}";
            _geoCache[player.IpAddress] = location ?? "—";
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
        var p = SelectedPlayer;
        Players.Remove(p);
        BannedPlayers.Add(p);
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
        var p = SelectedPlayer;
        Players.Remove(p);
        BannedPlayers.Add(p);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBannedPlayer))]
    private async Task PardonPlayerAsync()
    {
        if (_server is null || SelectedBannedPlayer is null) return;
        await _server.SendCommandAsync($"pardon {SelectedBannedPlayer.Username}");
        var p = SelectedBannedPlayer;
        BannedPlayers.Remove(p);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBannedPlayer))]
    private async Task PardonPlayerIpAsync()
    {
        if (_server is null || SelectedBannedPlayer is null) return;
        var target = SelectedBannedPlayer.IpAddress ?? SelectedBannedPlayer.Username;
        await _server.SendCommandAsync($"pardon-ip {target}");
        var p = SelectedBannedPlayer;
        BannedPlayers.Remove(p);
    }

    private async Task<string?> PromptReason(string prompt)
    {
        if (ShowReasonDialogAsync is not null)
            return await ShowReasonDialogAsync(prompt);
        return string.Empty; // no dialog wired — proceed without reason
    }

    private bool HasSelectedPlayer() => SelectedPlayer is not null;
    private bool HasSelectedBannedPlayer() => SelectedBannedPlayer is not null;

    partial void OnSelectedPlayerChanged(Player? value)
    {
        KickPlayerCommand.NotifyCanExecuteChanged();
        BanPlayerCommand.NotifyCanExecuteChanged();
        BanPlayerIpCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBannedPlayerChanged(Player? value)
    {
        PardonPlayerCommand.NotifyCanExecuteChanged();
        PardonPlayerIpCommand.NotifyCanExecuteChanged();
    }
}
