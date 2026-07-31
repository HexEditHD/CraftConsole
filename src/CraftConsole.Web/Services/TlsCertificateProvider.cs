using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CraftConsole.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace CraftConsole.Web.Services;

public enum TlsUploadResult { Success, InvalidPair, Pinned }

/// <summary>
/// Resolves and holds the certificate Kestrel serves over HTTPS.
///
/// Precedence: <c>--cert-path</c>/<c>CRAFTCONSOLE_CERT_PATH</c> (an operator-pinned PFX — wins
/// outright, and once pinned nothing else in this class can change <see cref="Current"/>) beats
/// whatever is stored in <c>tls-cert.json</c> (an uploaded cert/key pair, or the self-signed
/// certificate CraftConsole generated for itself) beats generating a fresh self-signed
/// certificate when nothing is stored yet.
///
/// <see cref="Current"/> is read by Kestrel's <c>ServerCertificateSelector</c> on every TLS
/// handshake (see Program.cs), so swapping it after a Settings upload takes effect immediately —
/// no restart. <see cref="InitializeAsync"/> must be awaited once, before <c>WebApplication.Build()</c>,
/// before anything reads <see cref="Current"/>.
///
/// Certificates are always loaded with the default key storage flags, never EphemeralKeySet —
/// an ephemeral RSA key fails the TLS handshake itself under Windows Schannel when used as a
/// Kestrel server certificate (confirmed: curl/Schannel reports "failed to receive handshake",
/// not a certificate-trust error), so it's unusable for serving, not just for re-export.
/// </summary>
public sealed class TlsCertificateProvider
{
    public const string EnvironmentVariable = "CRAFTCONSOLE_CERT_PATH";
    public const string PasswordEnvironmentVariable = "CRAFTCONSOLE_CERT_PASSWORD";
    public const string CommandLineSwitch = "--cert-path";
    public const string PasswordCommandLineSwitch = "--cert-password";

    private static readonly TimeSpan RegenerateWithinExpiry = TimeSpan.FromDays(30);

    private readonly JsonFileStore<StoredTlsCertificate> _store;
    private readonly IDataProtector _protector;
    private readonly ILogger<TlsCertificateProvider> _log;
    private readonly IReadOnlyList<string> _args;
    private readonly Func<string, string?> _environment;

    private readonly object _gate = new();
    private X509Certificate2? _current;

    public bool IsPinned { get; private set; }

    /// <summary>"pinned", "uploaded", or "self-signed" — reflects however <see cref="Current"/> was last set.</summary>
    public string Source { get; private set; } = "self-signed";

    public TlsCertificateProvider(
        SettingsHolder settings,
        IDataProtectionProvider dataProtection,
        ILogger<TlsCertificateProvider> logger,
        IReadOnlyList<string> args,
        Func<string, string?>? environment = null)
    {
        _store = new JsonFileStore<StoredTlsCertificate>(settings.AppDataPath, "tls-cert.json");
        _protector = dataProtection.CreateProtector("CraftConsole.TlsCertificateStore.v1");
        _log = logger;
        _args = args;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// The certificate Kestrel should present. Throws if <see cref="InitializeAsync"/> has not
    /// completed yet — that is a bug in startup ordering, not a runtime condition to recover from.
    /// </summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                return _current
                    ?? throw new InvalidOperationException(
                        $"{nameof(TlsCertificateProvider)}.{nameof(InitializeAsync)} must be awaited before {nameof(Current)} is read.");
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (TryReadArgOrEnv(CommandLineSwitch, EnvironmentVariable, out var pinnedPath))
        {
            var password = TryReadArgOrEnv(PasswordCommandLineSwitch, PasswordEnvironmentVariable, out var pw) ? pw : null;
            X509Certificate2 pinned;
            try
            {
                var bytes = await File.ReadAllBytesAsync(pinnedPath, ct);
                pinned = X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.DefaultKeySet);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not load the certificate pinned via {CommandLineSwitch} (\"{pinnedPath}\"): {ex.Message}", ex);
            }

            lock (_gate) { _current = pinned; }
            IsPinned = true;
            Source = "pinned";
            _log.LogInformation("Serving the TLS certificate pinned via {Switch}.", CommandLineSwitch);
            return;
        }

        var (cert, source) = await LoadOrGenerateAsync(ct);
        lock (_gate) { _current = cert; }
        Source = source;
    }

    /// <summary>
    /// Validates the uploaded certificate/key pair and, if it checks out, makes it the active
    /// certificate immediately. Rejects outright if a certificate is pinned via configuration —
    /// an upload that silently got overridden on the next restart would be worse than refusing it.
    /// </summary>
    public async Task<TlsUploadResult> TrySetUploadedAsync(string certPem, string keyPem, CancellationToken ct = default)
    {
        if (IsPinned) return TlsUploadResult.Pinned;

        byte[] pfxBytes;
        try
        {
            pfxBytes = CombineToPfx(certPem, keyPem);
        }
        catch (CryptographicException)
        {
            return TlsUploadResult.InvalidPair;
        }
        catch (FormatException)
        {
            return TlsUploadResult.InvalidPair;
        }

        var loaded = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.DefaultKeySet);

        await _store.SaveAsync(new StoredTlsCertificate(_protector.Protect(Convert.ToBase64String(pfxBytes)), "uploaded"));

        lock (_gate) { _current = loaded; }
        Source = "uploaded";
        _log.LogInformation("Now serving an uploaded TLS certificate (expires {Expiry:u}).", loaded.NotAfter);
        return TlsUploadResult.Success;
    }

    private async Task<(X509Certificate2 Cert, string Source)> LoadOrGenerateAsync(CancellationToken ct)
    {
        var stored = await _store.LoadAsync();
        if (stored is not null)
        {
            try
            {
                var pfxBytes = Convert.FromBase64String(_protector.Unprotect(stored.ProtectedPfx));
                var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.DefaultKeySet);

                if (cert.NotAfter - DateTimeOffset.UtcNow > RegenerateWithinExpiry)
                    return (cert, stored.Source);

                _log.LogWarning(
                    "The stored TLS certificate expires {Expiry:u}, which is soon — generating a fresh self-signed one.",
                    cert.NotAfter);
            }
            catch (CryptographicException ex)
            {
                _log.LogWarning(ex,
                    "The stored TLS certificate could not be decrypted (the Data Protection key ring likely changed) — generating a fresh self-signed one.");
            }
            catch (FormatException ex)
            {
                _log.LogWarning(ex, "The stored TLS certificate was unreadable — generating a fresh self-signed one.");
            }
        }

        // Export for storage, then reload from those same bytes for actual use — so the object
        // this run hands to Kestrel is shaped identically to what a later run loads from disk.
        using var generated = GenerateSelfSigned();
        var exported = generated.Export(X509ContentType.Pkcs12);
        await _store.SaveAsync(new StoredTlsCertificate(_protector.Protect(Convert.ToBase64String(exported)), "self-signed"));
        return (X509CertificateLoader.LoadPkcs12(exported, password: null, X509KeyStorageFlags.DefaultKeySet), "self-signed");
    }

    private static X509Certificate2 GenerateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=CraftConsole"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var serverAuthEku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }; // Server Authentication
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuthEku, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName) && !hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                sanBuilder.AddDnsName(hostName);
        }
        catch { /* best-effort — loopback SANs above are enough for the panel to work either way */ }
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);

        // Returned as-is, still freely exportable — the caller exports this for storage before
        // reloading it (or a later run's stored bytes) with EphemeralKeySet for actual use.
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>
    /// Combines a PEM certificate (optionally a full chain — leaf followed by intermediates) and
    /// a PEM private key into one PFX. Bundling the intermediates into the same PFX lets Kestrel
    /// serve the full chain, not just the leaf. Throws <see cref="CryptographicException"/> if the
    /// key doesn't correspond to the leaf certificate's public key.
    /// </summary>
    private static byte[] CombineToPfx(string certPem, string keyPem)
    {
        using var leafWithKey = X509Certificate2.CreateFromPem(certPem, keyPem);

        var allCerts = new X509Certificate2Collection();
        allCerts.ImportFromPem(certPem);

        var bundle = new X509Certificate2Collection { leafWithKey };
        for (var i = 1; i < allCerts.Count; i++)
            bundle.Add(allCerts[i]);

        return bundle.Export(X509ContentType.Pkcs12)
            ?? throw new CryptographicException("Failed to export the combined certificate.");
    }

    /// <summary>Accepts both "--flag value" and "--flag=value", same shape as DataPath's switch parsing.</summary>
    private bool TryReadArgOrEnv(string flag, string envVar, out string value)
    {
        for (var i = 0; i < _args.Count; i++)
        {
            var arg = _args[i];

            if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(flag.Length + 1)..].Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) return true;
            }

            if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase)
                && i + 1 < _args.Count
                && !string.IsNullOrWhiteSpace(_args[i + 1]))
            {
                value = _args[i + 1].Trim('"');
                return true;
            }
        }

        if (_environment(envVar) is { } fromEnv && !string.IsNullOrWhiteSpace(fromEnv))
        {
            value = fromEnv;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>Wire shape for tls-cert.json. ProtectedPfx is a Data-Protection-protected base64 PFX.</summary>
public sealed record StoredTlsCertificate(string ProtectedPfx, string Source);
