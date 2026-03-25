using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Console.ViewModels;

public partial class ConsoleViewModel : ObservableObject
{
    private IMinecraftServer? _server;
    private IDisposable? _subscription;
    private readonly HttpClient _http = new();

    // ── Output ──────────────────────────────────────────────────────────
    public ObservableCollection<ConsoleEntry> Entries { get; } = [];
    [ObservableProperty] private bool _hasEntries;

    // ── Players sidebar ──────────────────────────────────────────────────
    public ObservableCollection<PlayerAvatarItem> ConnectedPlayers { get; } = [];
    [ObservableProperty] private int _connectedPlayerCount;

    // ── Auto-scroll ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _autoScroll = true;

    public ConsoleViewModel()
    {
        Entries.CollectionChanged          += (_, _) => HasEntries             = Entries.Count > 0;
        ConnectedPlayers.CollectionChanged += (_, _) => ConnectedPlayerCount   = ConnectedPlayers.Count;
    }

    // ── Server quick-action commands (set by MainWindowViewModel) ────────
    public IRelayCommand? StartServerCommand { get; set; }
    public IRelayCommand? StopServerCommand  { get; set; }

    // ── Input ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _commandInput = string.Empty;
    [ObservableProperty] private bool _isConnected;

    // ── History (↑↓ navigation) ─────────────────────────────────────────
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    // ── Autocomplete suggestions ─────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _suggestions = [];
    [ObservableProperty] private bool _showSuggestions;

    private static readonly string[] MinecraftCommands =
    [
        "/advancement", "/attribute", "/ban", "/ban-ip", "/banlist", "/bossbar",
        "/clear", "/clone", "/damage", "/data", "/datapack", "/debug",
        "/defaultgamemode", "/deop", "/difficulty", "/effect", "/enchant",
        "/execute", "/experience", "/fill", "/fillbiome", "/forceload",
        "/function", "/gamemode", "/gamerule", "/give", "/help", "/item",
        "/kick", "/kill", "/list", "/locate", "/loot", "/me", "/msg",
        "/op", "/pardon", "/pardon-ip", "/particle", "/perf", "/place",
        "/playsound", "/recipe", "/reload", "/return", "/ride",
        "/save-all", "/save-off", "/save-on", "/say", "/schedule",
        "/scoreboard", "/seed", "/setblock", "/setworldspawn",
        "/spawnpoint", "/spreadplayers", "/stop", "/stopsound",
        "/summon", "/tag", "/team", "/teleport", "/tell", "/tellraw",
        "/time", "/title", "/tp", "/trigger", "/weather",
        "/whitelist", "/worldborder", "/xp",
    ];

    // ── Server attachment ────────────────────────────────────────────────

    public void Attach(IMinecraftServer server)
    {
        _subscription?.Dispose();
        _server = server;
        IsConnected = true;
        ConnectedPlayers.Clear();

        _subscription = server.ConsoleOutput.Subscribe(entry =>
        {
            var evt = ServerEventParser.TryParse(entry);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Entries.Add(entry);

                switch (evt)
                {
                    case PlayerJoinedEvent j:
                        if (!ConnectedPlayers.Any(p => p.Username == j.Player.Username))
                        {
                            var item = new PlayerAvatarItem(j.Player.Username);
                            ConnectedPlayers.Add(item);
                            _ = LoadAvatarAsync(item);
                        }
                        break;
                    case PlayerLeftEvent l:
                        var leaving = ConnectedPlayers.FirstOrDefault(p => p.Username == l.Username);
                        if (leaving is not null) ConnectedPlayers.Remove(leaving);
                        break;
                }
            });
        });
    }

    public void Detach()
    {
        _subscription?.Dispose();
        _server = null;
        IsConnected = false;
        ConnectedPlayers.Clear();
    }

    private async Task LoadAvatarAsync(PlayerAvatarItem item)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"https://mc-heads.net/avatar/{Uri.EscapeDataString(item.Username)}/32");
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync();
            // Bitmap must be created on a background thread; UI update via ObservableProperty
            var bmp = new Bitmap(stream);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => item.Avatar = bmp);
        }
        catch { /* network unavailable — fallback letter stays */ }
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SendCommandAsync()
    {
        var cmd = CommandInput.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;

        if (_history.Count == 0 || _history[^1] != cmd)
            _history.Add(cmd);
        _historyIndex = -1;

        ShowSuggestions = false;
        CommandInput = string.Empty;

        Entries.Add(new ConsoleEntry(
            DateTimeOffset.Now,
            Raw: $"> {cmd}",
            Message: $"> {cmd}",
            Level: ConsoleEntryLevel.Input));

        if (_server is null)
        {
            Entries.Add(new ConsoleEntry(DateTimeOffset.Now,
                Raw: "No server running.", Message: "No server running.", Level: ConsoleEntryLevel.Error));
            return;
        }

        var processCmd = cmd.StartsWith('/') ? cmd[1..] : cmd;
        await _server.SendCommandAsync(processCmd);
    }

    [RelayCommand]
    private void ClearConsole() => Entries.Clear();

    [RelayCommand]
    private void ToggleAutoScroll() => AutoScroll = !AutoScroll;

    // ── Called from code-behind on KeyDown ────────────────────────────────

    public void HistoryUp()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == -1)
            _historyIndex = _history.Count - 1;
        else if (_historyIndex > 0)
            _historyIndex--;
        CommandInput = _history[_historyIndex];
    }

    public void HistoryDown()
    {
        if (_historyIndex == -1) return;
        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            CommandInput = _history[_historyIndex];
        }
        else
        {
            _historyIndex = -1;
            CommandInput = string.Empty;
        }
    }

    public void AcceptSuggestion(string suggestion)
    {
        CommandInput = suggestion + " ";
        ShowSuggestions = false;
    }

    public void DismissSuggestions() => ShowSuggestions = false;

    // ── Reactive: update suggestions as user types ────────────────────────

    partial void OnCommandInputChanged(string value)
    {
        if (!value.StartsWith('/') || value.Length == 0)
        {
            Suggestions.Clear();
            ShowSuggestions = false;
            return;
        }

        var matches = MinecraftCommands
            .Where(c => c.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();

        Suggestions.Clear();
        foreach (var m in matches) Suggestions.Add(m);
        ShowSuggestions = matches.Count > 0;
    }
}
