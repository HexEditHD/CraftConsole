using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CraftConsole.Tests.Web;

public class TlsCertificateProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly SettingsHolder _settings;

    public TlsCertificateProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-tls-cert-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _settings = new SettingsHolder(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Same trick as RconSecretStoreTests: instances sharing a key-ring folder can
    /// decrypt each other's stored certificate; instances on different folders cannot — used
    /// to simulate a key ring that changed out from under a stored certificate.</summary>
    private TlsCertificateProvider NewProvider(
        string keyRingFolder = "dpkeys",
        IReadOnlyList<string>? args = null,
        Func<string, string?>? environment = null)
        => new(_settings,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, keyRingFolder))),
            NullLogger<TlsCertificateProvider>.Instance,
            args ?? [],
            environment);

    private static (string CertPem, string KeyPem) GenerateTestCertPem(string subject = "CN=test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    private static async Task<string> WritePfxAsync(string dir, string fileName, string certPem, string keyPem)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
        var path = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(path, cert.Export(X509ContentType.Pkcs12));
        return path;
    }

    [Fact]
    public async Task Generates_a_self_signed_certificate_with_the_expected_shape_by_default()
    {
        var provider = NewProvider();

        await provider.InitializeAsync();

        Assert.False(provider.IsPinned);
        Assert.Equal("self-signed", provider.Source);

        var cert = provider.Current;
        Assert.Equal("CN=CraftConsole", cert.Subject);
        Assert.True(cert.HasPrivateKey);
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(4));

        // Looked up by OID value, not Oid.FriendlyName — the friendly-name lookup table is
        // platform-specific (populated on Windows, empty for these OIDs under Linux's
        // OpenSSL-backed implementation), while the dotted OID itself is portable.
        var san = cert.Extensions["2.5.29.17"] ?? throw new InvalidOperationException("No SAN extension found.");
        var sanText = san.Format(false);
        Assert.Contains("localhost", sanText);
        Assert.Contains("127.0.0.1", sanText);

        var eku = cert.Extensions["2.5.29.37"] ?? throw new InvalidOperationException("No EKU extension found.");
        Assert.Contains("1.3.6.1.5.5.7.3.1", eku.Format(false));
    }

    [Fact]
    public async Task Uploading_a_valid_certificate_and_key_pair_takes_effect_immediately()
    {
        var provider = NewProvider();
        await provider.InitializeAsync();
        var originalThumbprint = provider.Current.Thumbprint;

        var (certPem, keyPem) = GenerateTestCertPem("CN=uploaded-test");
        var result = await provider.TrySetUploadedAsync(certPem, keyPem);

        Assert.Equal(TlsUploadResult.Success, result);
        Assert.Equal("uploaded", provider.Source);
        Assert.NotEqual(originalThumbprint, provider.Current.Thumbprint);
        Assert.Equal("CN=uploaded-test", provider.Current.Subject);
    }

    [Fact]
    public async Task Uploading_a_mismatched_certificate_and_key_is_rejected_and_leaves_the_previous_certificate_active()
    {
        var provider = NewProvider();
        await provider.InitializeAsync();
        var originalThumbprint = provider.Current.Thumbprint;

        var (certPem, _) = GenerateTestCertPem("CN=cert-a");
        var (_, otherKeyPem) = GenerateTestCertPem("CN=cert-b");

        var result = await provider.TrySetUploadedAsync(certPem, otherKeyPem);

        Assert.Equal(TlsUploadResult.InvalidPair, result);
        Assert.Equal(originalThumbprint, provider.Current.Thumbprint);
        Assert.Equal("self-signed", provider.Source);
    }

    [Fact]
    public async Task Uploading_unparseable_content_is_rejected_as_an_invalid_pair_not_an_unhandled_exception()
    {
        var provider = NewProvider();
        await provider.InitializeAsync();

        var result = await provider.TrySetUploadedAsync("not a certificate", "not a key");

        Assert.Equal(TlsUploadResult.InvalidPair, result);
    }

    [Fact]
    public async Task Round_trips_the_generated_certificate_across_instances_sharing_the_same_key_ring()
    {
        var first = NewProvider();
        await first.InitializeAsync();
        var thumbprint = first.Current.Thumbprint;

        var second = NewProvider();
        await second.InitializeAsync();

        Assert.Equal(thumbprint, second.Current.Thumbprint);
        Assert.Equal("self-signed", second.Source);
    }

    [Fact]
    public async Task Round_trips_an_uploaded_certificate_across_instances_sharing_the_same_key_ring()
    {
        var first = NewProvider();
        await first.InitializeAsync();
        var (certPem, keyPem) = GenerateTestCertPem("CN=uploaded-persisted");
        await first.TrySetUploadedAsync(certPem, keyPem);
        var thumbprint = first.Current.Thumbprint;

        var second = NewProvider();
        await second.InitializeAsync();

        Assert.Equal(thumbprint, second.Current.Thumbprint);
        Assert.Equal("uploaded", second.Source);
    }

    [Fact]
    public async Task A_store_that_cannot_be_decrypted_regenerates_instead_of_throwing()
    {
        await NewProvider("dpkeys-a").InitializeAsync();

        // Same tls-cert.json (both providers share _dir), but this instance's protector was
        // built from a different key ring, so Unprotect fails — must fall back to a fresh
        // self-signed certificate rather than throw or leave Current unset.
        var withDifferentKeyRing = NewProvider("dpkeys-b");
        await withDifferentKeyRing.InitializeAsync();

        Assert.Equal("self-signed", withDifferentKeyRing.Source);
        Assert.True(withDifferentKeyRing.Current.HasPrivateKey);
    }

    [Fact]
    public async Task A_certificate_pinned_via_cert_path_wins_over_anything_stored()
    {
        // A provider generates and stores a self-signed certificate normally first...
        await NewProvider().InitializeAsync();

        // ...then a *different* certificate is pinned via --cert-path; that one must win.
        var (certPem, keyPem) = GenerateTestCertPem("CN=pinned-test");
        var pfxPath = await WritePfxAsync(_dir, "pinned.pfx", certPem, keyPem);

        var provider = NewProvider(args: [TlsCertificateProvider.CommandLineSwitch, pfxPath]);
        await provider.InitializeAsync();

        Assert.True(provider.IsPinned);
        Assert.Equal("pinned", provider.Source);
        Assert.Equal("CN=pinned-test", provider.Current.Subject);
    }

    [Fact]
    public async Task A_pinned_certificate_rejects_uploads_instead_of_silently_ignoring_them()
    {
        var (certPem, keyPem) = GenerateTestCertPem("CN=pinned-test");
        var pfxPath = await WritePfxAsync(_dir, "pinned.pfx", certPem, keyPem);

        var provider = NewProvider(args: [TlsCertificateProvider.CommandLineSwitch, pfxPath]);
        await provider.InitializeAsync();

        var (uploadCertPem, uploadKeyPem) = GenerateTestCertPem("CN=should-not-apply");
        var result = await provider.TrySetUploadedAsync(uploadCertPem, uploadKeyPem);

        Assert.Equal(TlsUploadResult.Pinned, result);
        Assert.Equal("CN=pinned-test", provider.Current.Subject);
    }

    [Fact]
    public async Task Cert_path_can_be_supplied_via_the_environment_variable()
    {
        var (certPem, keyPem) = GenerateTestCertPem("CN=env-pinned");
        var pfxPath = await WritePfxAsync(_dir, "env-pinned.pfx", certPem, keyPem);

        var provider = NewProvider(
            args: [],
            environment: name => name == TlsCertificateProvider.EnvironmentVariable ? pfxPath : null);
        await provider.InitializeAsync();

        Assert.True(provider.IsPinned);
        Assert.Equal("CN=env-pinned", provider.Current.Subject);
    }

    [Fact]
    public async Task The_command_line_switch_takes_precedence_over_the_environment_variable()
    {
        var (argsCertPem, argsKeyPem) = GenerateTestCertPem("CN=from-args");
        var argsPfxPath = await WritePfxAsync(_dir, "from-args.pfx", argsCertPem, argsKeyPem);

        var (envCertPem, envKeyPem) = GenerateTestCertPem("CN=from-env");
        var envPfxPath = await WritePfxAsync(_dir, "from-env.pfx", envCertPem, envKeyPem);

        var provider = NewProvider(
            args: [TlsCertificateProvider.CommandLineSwitch, argsPfxPath],
            environment: name => name == TlsCertificateProvider.EnvironmentVariable ? envPfxPath : null);
        await provider.InitializeAsync();

        Assert.Equal("CN=from-args", provider.Current.Subject);
    }

    [Fact]
    public async Task An_unreadable_cert_path_fails_startup_loudly_instead_of_silently_falling_back()
    {
        var provider = NewProvider(
            args: [TlsCertificateProvider.CommandLineSwitch, Path.Combine(_dir, "does-not-exist.pfx")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InitializeAsync());
    }

    [Fact]
    public void Current_throws_before_InitializeAsync_has_completed()
    {
        var provider = NewProvider();

        Assert.Throws<InvalidOperationException>(() => provider.Current);
    }
}
