using System.Text.Json;
using CraftConsole.Core.Models;

namespace CraftConsole.Infrastructure.Http;

/// <summary>
/// Resolves, lists, and downloads server JARs for supported server types.
/// Supported: Vanilla, Paper, Purpur.
/// Unsupported (manual download): Spigot, Fabric, Forge.
/// </summary>
public class ServerDownloadService
{
    private readonly HttpClient _http;
    private readonly DownloadService _downloader;

    public ServerDownloadService(HttpClient http, DownloadService downloader)
    {
        _http = http;
        _downloader = downloader;
    }

    /// <summary>
    /// Returns up to 25 available version strings, newest first.
    /// Returns an empty list for types without an automated API.
    /// </summary>
    public Task<List<string>> FetchVersionsAsync(
        ServerType type, CancellationToken ct = default)
        => type switch
        {
            ServerType.Paper   => FetchPaperVersionsAsync(ct),
            ServerType.Vanilla => FetchVanillaVersionsAsync(ct),
            ServerType.Purpur  => FetchPurpurVersionsAsync(ct),
            _                  => Task.FromResult(new List<string>())
        };

    /// <summary>
    /// Resolves the download URL for the given version (or the latest if version is null).
    /// </summary>
    /// <exception cref="NotSupportedException">For types without an automated API.</exception>
    public Task<(string Version, string Url)> ResolveVersionAsync(
        ServerType type, string? version = null, CancellationToken ct = default)
        => type switch
        {
            ServerType.Paper   => ResolvePaperAsync(version, ct),
            ServerType.Vanilla => ResolveVanillaAsync(version, ct),
            ServerType.Purpur  => ResolvePurpurAsync(version, ct),
            _ => throw new NotSupportedException(
                $"{type} requires manual installation. Visit the official website to download.")
        };

    public Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => _downloader.DownloadFileAsync(url, destinationPath, progress, ct);

    // ── Version lists ───────────────────────────────────────────────────

    private async Task<List<string>> FetchPaperVersionsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("https://api.papermc.io/v2/projects/paper", ct);
        r.EnsureSuccessStatusCode();
        using var d = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        return d.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .Reverse()
            .Take(25)
            .ToList();
    }

    private async Task<List<string>> FetchVanillaVersionsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        r.EnsureSuccessStatusCode();
        using var d = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        return d.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => v.GetProperty("id").GetString()!)
            .Take(25)
            .ToList();
    }

    private async Task<List<string>> FetchPurpurVersionsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("https://api.purpurmc.org/v2/purpur", ct);
        r.EnsureSuccessStatusCode();
        using var d = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        return d.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .Reverse()
            .Take(25)
            .ToList();
    }

    // ── Resolution ──────────────────────────────────────────────────────

    private async Task<(string, string)> ResolvePaperAsync(string? version, CancellationToken ct)
    {
        // Resolve version if not specified
        if (string.IsNullOrEmpty(version))
        {
            var versions = await FetchPaperVersionsAsync(ct);
            version = versions[0];
        }

        using var r = await _http.GetAsync(
            $"https://api.papermc.io/v2/projects/paper/versions/{version}/builds", ct);
        r.EnsureSuccessStatusCode();
        using var d = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var builds = d.RootElement.GetProperty("builds").EnumerateArray().ToList();
        var buildNum = builds[^1].GetProperty("build").GetInt32();

        var url = $"https://api.papermc.io/v2/projects/paper/versions/{version}" +
                  $"/builds/{buildNum}/downloads/paper-{version}-{buildNum}.jar";
        return (version, url);
    }

    private async Task<(string, string)> ResolveVanillaAsync(string? version, CancellationToken ct)
    {
        using var r1 = await _http.GetAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        r1.EnsureSuccessStatusCode();
        using var d1 = await JsonDocument.ParseAsync(await r1.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        // Find the target version entry (or the latest release if not specified)
        var entry = string.IsNullOrEmpty(version)
            ? d1.RootElement.GetProperty("versions").EnumerateArray()
                .First(v => v.GetProperty("type").GetString() == "release")
            : d1.RootElement.GetProperty("versions").EnumerateArray()
                .First(v => v.GetProperty("id").GetString() == version);

        var versionId  = entry.GetProperty("id").GetString()!;
        var versionUrl = entry.GetProperty("url").GetString()!;

        using var r2 = await _http.GetAsync(versionUrl, ct);
        r2.EnsureSuccessStatusCode();
        using var d2 = await JsonDocument.ParseAsync(await r2.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var serverUrl = d2.RootElement
            .GetProperty("downloads")
            .GetProperty("server")
            .GetProperty("url")
            .GetString()!;

        return (versionId, serverUrl);
    }

    private async Task<(string, string)> ResolvePurpurAsync(string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version))
        {
            var versions = await FetchPurpurVersionsAsync(ct);
            version = versions[0];
        }

        var url = $"https://api.purpurmc.org/v2/purpur/{version}/latest/download";
        return (version, url);
    }
}
