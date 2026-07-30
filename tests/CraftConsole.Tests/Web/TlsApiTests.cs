using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// HTTP-level coverage for /api/tls/*, following CapabilityGatingTests' shape. HTTPS is on by
/// default with no args (see Program.cs), so WebApplicationFactory's real startup path — cert
/// resolution, DI registration, MapTlsApi — all runs; only the actual Kestrel TLS handshake is
/// out of reach here (TestServer is in-memory, see CapabilityGatingTests' own note on that same
/// limitation for loopback checks) and was verified manually instead.
/// </summary>
[Collection(nameof(WebAppFactoryCollection))]
public sealed class TlsApiTests : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TlsApiTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cc-tls-api-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, _dataDir);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var auth = _factory.Services.GetRequiredService<AuthService>();
        var token = auth.CreateSession();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthApi.CookieName}={token}");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private static (string CertPem, string KeyPem) GenerateTestCertPem(string subject = "CN=test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    private static MultipartFormDataContent UploadForm(string certPem, string keyPem)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(certPem), "certificate", "cert.pem");
        form.Add(new StringContent(keyPem), "key", "key.pem");
        return form;
    }

    [Fact]
    public async Task Status_reports_a_self_signed_certificate_by_default()
    {
        var status = await (await _client.GetAsync("/api/tls/status")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("self-signed", status.GetProperty("source").GetString());
        Assert.False(status.GetProperty("pinned").GetBoolean());
        Assert.True(status.GetProperty("expiry").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public async Task Unauthenticated_status_requests_are_rejected()
    {
        using var anonymous = _factory.CreateClient();

        var res = await anonymous.GetAsync("/api/tls/status");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Uploading_a_valid_pair_succeeds_and_updates_status()
    {
        var (certPem, keyPem) = GenerateTestCertPem("CN=uploaded-via-api");

        var res = await _client.PostAsync("/api/tls/certificate", UploadForm(certPem, keyPem));

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("uploaded", body.GetProperty("source").GetString());

        var status = await (await _client.GetAsync("/api/tls/status")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("uploaded", status.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Uploading_a_mismatched_pair_is_rejected_with_a_clear_message()
    {
        var (certPem, _) = GenerateTestCertPem("CN=cert-a");
        var (_, otherKeyPem) = GenerateTestCertPem("CN=cert-b");

        var res = await _client.PostAsync("/api/tls/certificate", UploadForm(certPem, otherKeyPem));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("match", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uploading_without_both_files_is_rejected()
    {
        var (certPem, keyPem) = GenerateTestCertPem();
        var certOnly = new MultipartFormDataContent { { new StringContent(certPem), "certificate", "cert.pem" } };

        var res = await _client.PostAsync("/api/tls/certificate", certOnly);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        _ = keyPem; // unused on this path — only the certificate field is sent, deliberately
    }

    /// <summary>
    /// The "pinned via config" scenario needs CRAFTCONSOLE_CERT_PATH set before the factory
    /// boots. That's process-wide state, so — unlike every other test here, which shares this
    /// class's constructor-built factory — this one is fully self-contained: it sets the env
    /// var, builds its own local factory, asserts, and clears the var in a finally block. That
    /// keeps its lifetime inside one test method with no handoff to or from sibling tests, so it
    /// can't race another instance's constructor/dispose regardless of xUnit's scheduling.
    /// </summary>
    [Fact]
    public async Task A_certificate_pinned_via_config_is_reported_and_rejects_uploads()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cc-tls-api-pinned-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dataDir);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pinned-via-env", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var pinnedCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfxPath = Path.Combine(dataDir, "pinned.pfx");
        await File.WriteAllBytesAsync(pfxPath, pinnedCert.Export(X509ContentType.Pkcs12));

        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, dataDir);
        Environment.SetEnvironmentVariable(TlsCertificateProvider.EnvironmentVariable, pfxPath);
        try
        {
            await using var pinnedFactory = new WebApplicationFactory<Program>();
            using var pinnedClient = pinnedFactory.CreateClient();
            var auth = pinnedFactory.Services.GetRequiredService<AuthService>();
            pinnedClient.DefaultRequestHeaders.Add("Cookie", $"{AuthApi.CookieName}={auth.CreateSession()}");

            var status = await (await pinnedClient.GetAsync("/api/tls/status")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pinned", status.GetProperty("source").GetString());
            Assert.True(status.GetProperty("pinned").GetBoolean());
            Assert.Equal("CN=pinned-via-env", status.GetProperty("subject").GetString());

            var (uploadCertPem, uploadKeyPem) = GenerateTestCertPem("CN=should-not-apply");
            var uploadRes = await pinnedClient.PostAsync("/api/tls/certificate", UploadForm(uploadCertPem, uploadKeyPem));
            Assert.Equal(HttpStatusCode.Conflict, uploadRes.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable(TlsCertificateProvider.EnvironmentVariable, null);
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }
}
