using System.Text.Json;
using System.Xml.Linq;
using CraftConsole.Core.Models;

namespace CraftConsole.Infrastructure.Http;

/// <summary>
/// Resolves, lists, and downloads server JARs for supported server types.
/// Supported: Vanilla, Paper, Purpur, Fabric, NeoForge.
/// Unsupported (manual download): Spigot, Forge — see SetupService.AllServerTypes for why
/// classic Forge stays manual even though NeoForge, its sibling, is automated.
/// </summary>
public class ServerDownloadService
{
    // fill.papermc.io and meta.fabricmc.net both expect a descriptive client
    // identifier rather than a bare/default one.
    private const string UserAgent = "CraftConsole (+https://github.com/HexEditHD/CraftConsole)";

    // ServerStarterJar wraps the real NeoForge installer so the result behaves like any other
    // single-jar server (see NeoForgeInstaller). One stable URL regardless of NeoForge version —
    // the version-specific installer is what it downloads on first run via --installer.
    private const string ServerStarterJarUrl =
        "https://github.com/neoforged/ServerStarterJar/releases/latest/download/server.jar";
    private const string NeoForgeMavenMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

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
            ServerType.Fabric  => FetchFabricVersionsAsync(ct),
            ServerType.NeoForge => FetchNeoForgeVersionsAsync(ct),
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
            ServerType.Fabric  => ResolveFabricAsync(version, ct),
            ServerType.NeoForge => ResolveNeoForgeAsync(version, ct),
            _ => throw new NotSupportedException(
                $"{type} requires manual installation. Visit the official website to download.")
        };

    /// <summary>GET with a descriptive User-Agent, without mutating the shared HttpClient's defaults.</summary>
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var r = await _http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => _downloader.DownloadFileAsync(url, destinationPath, progress, ct);

    // ── Version lists ───────────────────────────────────────────────────

    private async Task<List<string>> FetchPaperVersionsAsync(CancellationToken ct)
    {
        using var d = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", ct);

        // v3 groups versions by minor line (e.g. "26.2", "1.21"), newest line first,
        // each line's array newest patch first — already the order we want.
        return d.RootElement
            .GetProperty("versions")
            .EnumerateObject()
            .SelectMany(line => line.Value.EnumerateArray().Select(v => v.GetString()!))
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

    private async Task<List<string>> FetchFabricVersionsAsync(CancellationToken ct)
    {
        using var d = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/game", ct);

        return d.RootElement
            .EnumerateArray()
            .Where(v => v.GetProperty("stable").GetBoolean())
            .Select(v => v.GetProperty("version").GetString()!)
            .Take(25)
            .ToList();
    }

    private async Task<List<string>> FetchNeoForgeVersionsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, NeoForgeMavenMetadataUrl);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var r = await _http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();

        var doc = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));

        // Maven metadata lists <version> entries in publish order, not sorted — this repo mixes
        // several concurrently-maintained release lines (e.g. "21.1.248", "26.1.2.94",
        // "26.2.0.45-beta"), so the tail of the document is newest, not the numeric max.
        return [.. doc.Descendants("version")
            .Select(e => e.Value)
            .Reverse()
            .Take(25)];
    }

    // ── Resolution ──────────────────────────────────────────────────────

    private async Task<(string, string)> ResolvePaperAsync(string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version))
        {
            var versions = await FetchPaperVersionsAsync(ct);
            version = versions[0];
        }

        using var d = await GetJsonAsync(
            $"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds/latest", ct);

        // Content-addressed on a separate host (fill-data.papermc.io) — cannot be
        // derived from the version/build number, must be read verbatim.
        var url = d.RootElement
            .GetProperty("downloads")
            .GetProperty("server:default")
            .GetProperty("url")
            .GetString()!;
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

    private async Task<(string, string)> ResolveFabricAsync(string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version))
        {
            var versions = await FetchFabricVersionsAsync(ct);
            version = versions[0];
        }

        using var loaderDoc = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader", ct);
        var loader = loaderDoc.RootElement.EnumerateArray()
            .First(v => v.GetProperty("stable").GetBoolean())
            .GetProperty("version").GetString()!;

        using var installerDoc = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
        var installer = installerDoc.RootElement.EnumerateArray()
            .First(v => v.GetProperty("stable").GetBoolean())
            .GetProperty("version").GetString()!;

        // A ready-to-run server jar — no separate installer step, unlike Forge.
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{version}/{loader}/{installer}/server/jar";
        return (version, url);
    }

    /// <summary>
    /// Unlike every other type here, the "download" isn't the finished server — it's
    /// ServerStarterJar, a fixed URL regardless of version. SetupService runs the actual NeoForge
    /// installer against it afterward (see NeoForgeInstaller) before reporting completion.
    /// </summary>
    private async Task<(string, string)> ResolveNeoForgeAsync(string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version))
        {
            var versions = await FetchNeoForgeVersionsAsync(ct);
            version = versions[0];
        }

        return (version, ServerStarterJarUrl);
    }
}
