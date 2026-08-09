using System.Text.Json;

namespace CraftConsole.Infrastructure.Http;

public record ModrinthSearchHit(
    string ProjectId, string Slug, string Title, string Description, string Author,
    string? IconUrl, long Downloads, string ProjectType);

public record ModrinthSearchResult(List<ModrinthSearchHit> Hits, int TotalHits);

public record ModrinthFile(string Url, string FileName, bool Primary, long Size);

/// <summary>
/// DependencyType is "required", "optional", "incompatible", or "embedded" (already
/// bundled in the depending file — nothing to install). ProjectId/VersionId/FileName
/// are each nullable because Modrinth lets a dependency reference any subset of the
/// three; which one is present is what the caller resolves against.
/// </summary>
public record ModrinthDependency(string? VersionId, string? ProjectId, string? FileName, string DependencyType);

public record ModrinthVersion(
    string Id, string ProjectId, string Name, string VersionNumber,
    List<string> GameVersions, List<string> Loaders,
    List<ModrinthDependency> Dependencies, List<ModrinthFile> Files);

/// <summary>
/// Search, version listing, and single-version/-project lookups against the
/// Modrinth v2 API. Mirrors ServerDownloadService's shape: raw JsonDocument
/// parsing over a shared HttpClient, no generated client.
/// </summary>
public class ModrinthClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    // Modrinth asks API consumers for a descriptive User-Agent identifying the
    // application, same reasoning as ServerDownloadService's UA for fill.papermc.io.
    private const string UserAgent = "CraftConsole (+https://github.com/HexEditHD/CraftConsole)";

    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http)
    {
        _http = http;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var r = await _http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>
    /// loaders is an OR facet (any of these loaders matches) — a Paper profile
    /// searches "paper", "spigot" and "bukkit" together since plugins commonly
    /// declare only one of the three even when compatible with all.
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query, string projectType, IReadOnlyList<string> loaders, string? gameVersion,
        int offset, int limit, CancellationToken ct = default)
    {
        List<List<string>> facets = [[$"project_type:{projectType}"]];
        if (loaders.Count > 0) facets.Add([.. loaders.Select(l => $"categories:{l}")]);
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add([$"versions:{gameVersion}"]);

        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}" +
                   $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}" +
                   $"&offset={offset}&limit={limit}";

        using var d = await GetJsonAsync(url, ct);
        var root = d.RootElement;
        var hits = root.GetProperty("hits").EnumerateArray().Select(h => new ModrinthSearchHit(
            h.GetProperty("project_id").GetString()!,
            h.GetProperty("slug").GetString() ?? "",
            h.GetProperty("title").GetString() ?? "",
            h.GetProperty("description").GetString() ?? "",
            h.GetProperty("author").GetString() ?? "",
            h.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
            h.TryGetProperty("downloads", out var dl) ? dl.GetInt64() : 0,
            h.GetProperty("project_type").GetString() ?? ""))
            .ToList();

        return new ModrinthSearchResult(hits, root.GetProperty("total_hits").GetInt32());
    }

    /// <summary>Versions for one project, newest first (Modrinth's own default order), loader/game-version filtered.</summary>
    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(
        string projectId, IReadOnlyList<string> loaders, string? gameVersion, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        List<string> qs = [];
        if (loaders.Count > 0) qs.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(loaders))}");
        if (!string.IsNullOrEmpty(gameVersion)) qs.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (qs.Count > 0) url += "?" + string.Join("&", qs);

        using var d = await GetJsonAsync(url, ct);
        return [.. d.RootElement.EnumerateArray().Select(ParseVersion)];
    }

    public async Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        using var d = await GetJsonAsync($"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}", ct);
        return ParseVersion(d.RootElement);
    }

    /// <summary>Just the title — used to name a dependency in the install-required-deps prompt.</summary>
    public async Task<string> GetProjectTitleAsync(string projectId, CancellationToken ct = default)
    {
        using var d = await GetJsonAsync($"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}", ct);
        return d.RootElement.GetProperty("title").GetString() ?? projectId;
    }

    private static ModrinthVersion ParseVersion(JsonElement v)
    {
        var files = v.GetProperty("files").EnumerateArray().Select(f => new ModrinthFile(
            f.GetProperty("url").GetString()!,
            f.GetProperty("filename").GetString()!,
            f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
            f.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
            .ToList();

        var deps = v.TryGetProperty("dependencies", out var depArray)
            ? depArray.EnumerateArray().Select(dep => new ModrinthDependency(
                dep.TryGetProperty("version_id", out var vid) && vid.ValueKind == JsonValueKind.String ? vid.GetString() : null,
                dep.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null,
                dep.TryGetProperty("file_name", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() : null,
                dep.GetProperty("dependency_type").GetString() ?? "optional"))
                .ToList()
            : [];

        return new ModrinthVersion(
            v.GetProperty("id").GetString()!,
            v.GetProperty("project_id").GetString()!,
            v.GetProperty("name").GetString() ?? "",
            v.GetProperty("version_number").GetString() ?? "",
            [.. v.GetProperty("game_versions").EnumerateArray().Select(g => g.GetString()!)],
            [.. v.GetProperty("loaders").EnumerateArray().Select(l => l.GetString()!)],
            deps,
            files);
    }
}
