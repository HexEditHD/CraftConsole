using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

public static class TlsApi
{
    private const long MaxUploadBytes = 64 * 1024;

    public static void MapTlsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tls/status", (TlsCertificateProvider tls) =>
        {
            var cert = tls.Current;
            return Results.Json(new
            {
                Source = tls.Source,
                Pinned = tls.IsPinned,
                Expiry = cert.NotAfter,
                Thumbprint = cert.Thumbprint,
                Subject = cert.Subject,
            }, Json.Options);
        });

        app.MapPost("/api/tls/certificate", async (HttpRequest request, TlsCertificateProvider tls) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { Message = "Expected a multipart form with certificate and key files." });

            var form = await request.ReadFormAsync();
            var certFile = form.Files["certificate"];
            var keyFile = form.Files["key"];

            if (certFile is null || keyFile is null)
                return Results.BadRequest(new { Message = "Both a certificate and a private key file are required." });

            if (certFile.Length == 0 || keyFile.Length == 0)
                return Results.BadRequest(new { Message = "Certificate and key files can't be empty." });

            if (certFile.Length > MaxUploadBytes || keyFile.Length > MaxUploadBytes)
                return Results.BadRequest(new { Message = "Certificate and key files must each be under 64 KB." });

            var certPem = await ReadAsStringAsync(certFile);
            var keyPem = await ReadAsStringAsync(keyFile);

            var result = await tls.TrySetUploadedAsync(certPem, keyPem);
            return result switch
            {
                TlsUploadResult.Success => Results.Json(new
                {
                    Source = tls.Source,
                    Expiry = tls.Current.NotAfter,
                    Thumbprint = tls.Current.Thumbprint,
                }, Json.Options),
                TlsUploadResult.InvalidPair => Results.BadRequest(new
                    { Message = "The certificate and private key don't match, or couldn't be parsed as PEM." }),
                TlsUploadResult.Pinned => Results.Json(
                    new { Message = "A certificate is pinned via --cert-path; remove that to manage it here." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(),
            };
        });
    }

    private static async Task<string> ReadAsStringAsync(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}
