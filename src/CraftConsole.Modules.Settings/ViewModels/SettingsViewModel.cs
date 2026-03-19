using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Modules.Settings.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _appDataPath;

    // ── Console display ──────────────────────────────────────────────────
    public bool ShowTimestamp
    {
        get => _settings.ShowTimestamp;
        set { _settings.ShowTimestamp = value; OnPropertyChanged(); Save(); }
    }

    public bool ShowDate
    {
        get => _settings.ShowDate;
        set { _settings.ShowDate = value; OnPropertyChanged(); Save(); }
    }

    // ── Log level colors ─────────────────────────────────────────────────
    public string ColorInfo
    {
        get => _settings.ColorInfo;
        set { _settings.ColorInfo = value; OnPropertyChanged(); Save(); }
    }

    public string ColorWarn
    {
        get => _settings.ColorWarn;
        set { _settings.ColorWarn = value; OnPropertyChanged(); Save(); }
    }

    public string ColorError
    {
        get => _settings.ColorError;
        set { _settings.ColorError = value; OnPropertyChanged(); Save(); }
    }

    public string ColorPlayer
    {
        get => _settings.ColorPlayer;
        set { _settings.ColorPlayer = value; OnPropertyChanged(); Save(); }
    }

    // ── Preset swatches ──────────────────────────────────────────────────
    public static string[] InfoSwatches   => ["#60A5FA", "#93C5FD", "#3B82F6", "#94A3B8", "#38BDF8"];
    public static string[] WarnSwatches   => ["#FB923C", "#F97316", "#FDBA74", "#FCD34D", "#C2410C"];
    public static string[] ErrorSwatches  => ["#F87171", "#EF4444", "#FCA5A5", "#F43F5E", "#B91C1C"];
    public static string[] PlayerSwatches => ["#22C55E", "#16A34A", "#4ADE80", "#86EFAC", "#15803D"];

    public SettingsViewModel(AppSettings settings, string appDataPath)
    {
        _settings    = settings;
        _appDataPath = appDataPath;
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetInfoColor(string hex)   { ColorInfo   = hex; }

    [RelayCommand]
    private void SetWarnColor(string hex)   { ColorWarn   = hex; }

    [RelayCommand]
    private void SetErrorColor(string hex)  { ColorError  = hex; }

    [RelayCommand]
    private void SetPlayerColor(string hex) { ColorPlayer = hex; }

    [RelayCommand]
    private void ResetColors()
    {
        ColorInfo   = "#94A3B8";
        ColorWarn   = "#FB923C";
        ColorError  = "#F87171";
        ColorPlayer = "#22C55E";
    }

    private void Save() => _ = _settings.SaveAsync(_appDataPath);
}
