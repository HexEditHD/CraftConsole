using System.Text.Json;

namespace CraftConsole.Infrastructure.Http;

/// <summary>Describes a downloadable Java LTS version.</summary>
public record JavaVersionInfo(int Major, string DisplayName, bool IsLts);

public class JavaDownloadService
{
    private readonly HttpClient _http;
    private readonly DownloadService _downloader;

    private static readonly List<JavaVersionInfo> Fallback =
    [
        new(21, "Java 21 LTS", true),
        new(17, "Java 17 LTS", true),
        new(11, "Java 11 LTS", true),
        new(8,  "Java 8 LTS",  true),
    ];

    public JavaDownloadService(HttpClient http, DownloadService downloader)
    {
        _http = http;
        _downloader = downloader;
    }

    /// <summary>Fetches available LTS versions from Adoptium, newest first.</summary>
    public async Task<List<JavaVersionInfo>> FetchVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync(
                "https://api.adoptium.net/v3/info/available_releases", ct);
            r.EnsureSuccessStatusCode();
            using var d = await JsonDocument.ParseAsync(
                await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var lts = d.RootElement
                .GetProperty("available_lts_releases")
                .EnumerateArray()
                .Select(v => v.GetInt32())
                .OrderByDescending(v => v)
                .ToList();

            return lts
                .Select(v => new JavaVersionInfo(v, $"Java {v} LTS", true))
                .ToList();
        }
        catch
        {
            return Fallback;
        }
    }

    /// <summary>
    /// Resolves the MSI installer URL for the given Java major version (Windows x64, Eclipse Temurin).
    /// </summary>
    public async Task<(string FileName, string Url)> ResolveAsync(
        int major, CancellationToken ct = default)
    {
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                     "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";

        using var r = await _http.GetAsync(apiUrl, ct);
        r.EnsureSuccessStatusCode();
        using var d = await JsonDocument.ParseAsync(
            await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var assets = d.RootElement.EnumerateArray().ToList();
        if (assets.Count == 0)
            throw new InvalidOperationException($"No Temurin assets found for Java {major}.");

        // Prefer MSI, fall back to zip
        var pkg = assets
            .Select(a => a.GetProperty("binary").GetProperty("package"))
            .FirstOrDefault(p =>
            {
                var name = p.GetProperty("name").GetString() ?? "";
                return name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            });

        if (pkg.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            pkg = assets[0].GetProperty("binary").GetProperty("package");

        var fileName = pkg.GetProperty("name").GetString()!;
        var url      = pkg.GetProperty("link").GetString()!;
        return (fileName, url);
    }

    public Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => _downloader.DownloadFileAsync(url, destinationPath, progress, ct);
}
