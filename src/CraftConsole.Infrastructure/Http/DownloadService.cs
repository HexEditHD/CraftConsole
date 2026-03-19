namespace CraftConsole.Infrastructure.Http;

public class DownloadService
{
    private readonly HttpClient _http;

    public DownloadService(HttpClient http)
    {
        _http = http;
    }

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destinationPath);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0) progress?.Report((double)downloaded / total);
        }
    }
}
