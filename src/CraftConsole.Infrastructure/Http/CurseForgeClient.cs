using System.Net;
using System.Text.Json;

namespace CraftConsole.Infrastructure.Http;

public record CurseForgeSearchHit(
    int ModId, string Slug, string Name, string Summary, string Author, string? IconUrl, long Downloads);

public record CurseForgeSearchResult(List<CurseForgeSearchHit> Hits, int TotalHits);

/// <summary>
/// RelationType is normalized to Modrinth's own vocabulary ("required",
/// "optional", "incompatible", "embedded") rather than CurseForge's integer
/// codes, so CurseForgeService's dependency handling reads the same way
/// ModrinthService's does — see RelationTypeName below for the mapping.
/// </summary>
public record CurseForgeDependency(int ModId, string RelationType);

/// <summary>
/// DisplayName falls back to FileName when CurseForge omits it, so a caller
/// always has something to show — never blank. ReleaseType is normalized to
/// Modrinth's channel vocabulary ("release"/"beta"/"alpha") the same way
/// RelationType is, via ReleaseTypeName below.
/// </summary>
public record CurseForgeFile(
    int Id, int ModId, string FileName, string DisplayName, string? DownloadUrl, long FileLength,
    string ReleaseType, DateTimeOffset? FileDate,
    List<string> GameVersions, List<CurseForgeDependency> Dependencies);

/// <summary>
/// Search, mod-files listing, and single-file/-mod lookups against the
/// CurseForge API v1. Every request needs an API key — CurseForge's terms
/// require one, unlike Modrinth's open read API — passed per call rather
/// than baked into the client, since it's configurable at runtime via
/// Settings without restarting the process.
/// </summary>
public class CurseForgeClient
{
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private const string UserAgent = "CraftConsole (+https://github.com/HexEditHD/CraftConsole)";

    // CurseForge's own internal id for the "Minecraft" game.
    public const int MinecraftGameId = 432;

    private readonly HttpClient _http;

    public CurseForgeClient(HttpClient http)
    {
        _http = http;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(string url, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Add("x-api-key", apiKey);
        return await _http.SendAsync(req, ct);
    }

    /// <summary>
    /// Rate limiting is entirely unhandled otherwise — a 429 would fall through
    /// to EnsureSuccessStatusCode as an opaque HttpRequestException. Worth
    /// having now that CheckUpdatesAsync can fan out one request per tracked
    /// mod for a whole modpack.
    /// </summary>
    private static void ThrowIfRateLimited(HttpResponseMessage r)
    {
        if (r.StatusCode != HttpStatusCode.TooManyRequests) return;
        var retryAfter = r.Headers.RetryAfter?.Delta?.TotalSeconds
            ?? (r.Headers.RetryAfter?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
        var hint = retryAfter is > 0 ? $" Try again in {Math.Ceiling(retryAfter.Value)} seconds." : " Try again shortly.";
        throw new InvalidOperationException($"CurseForge is rate-limiting requests.{hint}");
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        using var r = await SendRequestAsync(url, apiKey, ct);
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("CurseForge rejected the configured API key.");
        ThrowIfRateLimited(r);
        r.EnsureSuccessStatusCode();

        return await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public async Task<CurseForgeSearchResult> SearchAsync(
        string apiKey, string query, int classId, int? modLoaderType, string? gameVersion,
        int offset, int limit, CancellationToken ct = default)
    {
        List<string> qs = [$"gameId={MinecraftGameId}", $"classId={classId}", $"index={offset}", $"pageSize={limit}"];
        if (!string.IsNullOrEmpty(query)) qs.Add($"searchFilter={Uri.EscapeDataString(query)}");
        if (modLoaderType is { } mlt) qs.Add($"modLoaderType={mlt}");
        if (!string.IsNullOrEmpty(gameVersion)) qs.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");

        using var d = await GetJsonAsync($"{BaseUrl}/mods/search?{string.Join("&", qs)}", apiKey, ct);
        var root = d.RootElement;

        var hits = root.GetProperty("data").EnumerateArray().Select(m => new CurseForgeSearchHit(
            m.GetProperty("id").GetInt32(),
            m.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
            m.GetProperty("name").GetString() ?? "",
            m.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "",
            m.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array && authors.GetArrayLength() > 0
                && authors[0].TryGetProperty("name", out var authorName) && authorName.ValueKind == JsonValueKind.String
                ? authorName.GetString()! : "",
            m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
                && logo.TryGetProperty("thumbnailUrl", out var thumb) && thumb.ValueKind == JsonValueKind.String
                ? thumb.GetString() : null,
            m.TryGetProperty("downloadCount", out var dl) && dl.ValueKind == JsonValueKind.Number && dl.TryGetDouble(out var downloads)
                ? (long)downloads : 0))
            .ToList();

        var total = root.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("totalCount", out var tc)
            ? tc.GetInt32()
            : hits.Count;

        return new CurseForgeSearchResult(hits, total);
    }

    /// <summary>
    /// Files for one mod, loader/game-version filtered. Requests CurseForge's
    /// documented max page size explicitly rather than relying on whatever the
    /// API's own default happens to be, and sorts the parsed result by
    /// fileDate descending rather than trusting CurseForge's own ordering —
    /// InstallAsync's dependency path and CheckUpdatesAsync both treat index 0
    /// as "newest compatible", so that has to be guaranteed here, not assumed.
    /// </summary>
    public async Task<List<CurseForgeFile>> GetModFilesAsync(
        string apiKey, int modId, int? modLoaderType, string? gameVersion, CancellationToken ct = default)
    {
        List<string> qs = ["pageSize=50"];
        if (modLoaderType is { } mlt) qs.Add($"modLoaderType={mlt}");
        if (!string.IsNullOrEmpty(gameVersion)) qs.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");

        using var d = await GetJsonAsync($"{BaseUrl}/mods/{modId}/files?{string.Join("&", qs)}", apiKey, ct);
        return [.. d.RootElement.GetProperty("data").EnumerateArray().Select(ParseFile).OrderByDescending(f => f.FileDate)];
    }

    public async Task<CurseForgeFile> GetFileAsync(string apiKey, int modId, int fileId, CancellationToken ct = default)
    {
        using var d = await GetJsonAsync($"{BaseUrl}/mods/{modId}/files/{fileId}", apiKey, ct);
        return ParseFile(d.RootElement.GetProperty("data"));
    }

    /// <summary>Just the name — used for a dependency's title in the install-required-deps prompt.</summary>
    public async Task<string> GetModNameAsync(string apiKey, int modId, CancellationToken ct = default)
    {
        using var d = await GetJsonAsync($"{BaseUrl}/mods/{modId}", apiKey, ct);
        return d.RootElement.GetProperty("data").GetProperty("name").GetString() ?? modId.ToString();
    }

    /// <summary>
    /// A file's own DownloadUrl is sometimes null — its author disabled
    /// third-party downloads for that file — in which case this dedicated
    /// endpoint is the other way to resolve one, and can itself come back
    /// empty for the same reason (nothing left to try after that).
    /// </summary>
    public async Task<string?> ResolveDownloadUrlAsync(string apiKey, int modId, int fileId, CancellationToken ct = default)
    {
        using var r = await SendRequestAsync($"{BaseUrl}/mods/{modId}/files/{fileId}/download-url", apiKey, ct);

        // A 403 here almost always means the file's own third-party-download
        // flag, not a bad key — by the time this runs, an earlier call in the
        // same install (GetFileAsync) has already succeeded with this key, so
        // a key rejection would have surfaced there first. A 404 means the
        // file is gone. Both are "nothing to resolve" — InstallOneAsync's own
        // friendly message covers a null return, which reads better than the
        // generic API-key-rejected error for either case.
        if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        if (r.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("CurseForge rejected the configured API key.");
        ThrowIfRateLimited(r);
        r.EnsureSuccessStatusCode();

        using var d = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return d.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;
    }

    private static CurseForgeFile ParseFile(JsonElement f)
    {
        var deps = f.TryGetProperty("dependencies", out var depArray) && depArray.ValueKind == JsonValueKind.Array
            ? depArray.EnumerateArray().Select(dep => new CurseForgeDependency(
                dep.GetProperty("modId").GetInt32(),
                RelationTypeName(dep.GetProperty("relationType").GetInt32())))
                .ToList()
            : [];

        var fileName = f.GetProperty("fileName").GetString() ?? "";
        var displayName = f.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()! : "";

        return new CurseForgeFile(
            f.GetProperty("id").GetInt32(),
            f.GetProperty("modId").GetInt32(),
            fileName,
            string.IsNullOrEmpty(displayName) ? fileName : displayName,
            f.TryGetProperty("downloadUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
            f.TryGetProperty("fileLength", out var len) ? len.GetInt64() : 0,
            f.TryGetProperty("releaseType", out var rt) && rt.ValueKind == JsonValueKind.Number ? ReleaseTypeName(rt.GetInt32()) : "",
            f.TryGetProperty("fileDate", out var fd) && fd.TryGetDateTimeOffset(out var fileDate) ? fileDate : null,
            f.TryGetProperty("gameVersions", out var gv) && gv.ValueKind == JsonValueKind.Array
                ? [.. gv.EnumerateArray().Select(g => g.GetString()!)]
                : [],
            deps);
    }

    // CurseForge's relationType: 1 EmbeddedLibrary, 2 OptionalDependency,
    // 3 RequiredDependency, 4 Tool, 5 Incompatible, 6 Include.
    private static string RelationTypeName(int relationType) => relationType switch
    {
        3 => "required",
        2 => "optional",
        5 => "incompatible",
        1 or 6 => "embedded",
        _ => "optional",
    };

    // CurseForge's releaseType: 1 Release, 2 Beta, 3 Alpha.
    private static string ReleaseTypeName(int releaseType) => releaseType switch
    {
        1 => "release",
        2 => "beta",
        3 => "alpha",
        _ => "",
    };
}
