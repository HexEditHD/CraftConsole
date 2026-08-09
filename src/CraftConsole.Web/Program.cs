using System.Diagnostics;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Infrastructure.Logging;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

// The configuration binder reads "--key value" as a pair, so a valueless flag
// swallows whatever follows it: "--no-browser --urls http://…" bound the default
// address because --urls became the *value* of --no-browser. Flags CraftConsole
// interprets itself are removed before the host ever sees them.
string[] ownFlags = ["--no-browser", "--http"];
var hostArgs = args.Where(a => !ownFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

// HTTPS by default — a self-signed certificate is generated on first run (see
// TlsCertificateProvider below), same posture as most self-hosted admin panels. --http /
// CRAFTCONSOLE_HTTPS=0 opts back out to plain HTTP, e.g. for anyone already behind a
// TLS-terminating reverse proxy.
var httpsEnabled = !args.Contains("--http", StringComparer.OrdinalIgnoreCase)
    && Environment.GetEnvironmentVariable("CRAFTCONSOLE_HTTPS") != "0";

// Localhost only unless the user explicitly overrides via --urls / ASPNETCORE_URLS.
// A password is required for every request once one is set up (see AuthService /
// the auth gate below), so widening the bind is reasonable after setup — but do
// it over a trusted network or a tunnel.
if (builder.Configuration["urls"] is null
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls($"{(httpsEnabled ? "https" : "http")}://127.0.0.1:5178");
}

// --data-dir → CRAFTCONSOLE_DATA → per-user OS default. Created eagerly so a
// permissions problem surfaces at startup rather than on the first write.
var appDataPath = DataPath.Resolve(args);
Directory.CreateDirectory(appDataPath);

// Route the standard ILogger pipeline through Serilog so it also lands in
// {dataDir}/logs. Without this there is no on-disk trace at all — useful when
// the panel runs headless as a service.
var serilog = AppLogger.Create(appDataPath, builder.Environment.IsDevelopment());
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(serilog, dispose: true);

var settingsHolder = new SettingsHolder(appDataPath);
builder.Services.AddSingleton(settingsHolder);
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<ServerDownloadService>();
builder.Services.AddSingleton<JavaDownloadService>();

// Key ring pinned to the app data directory: the default location ignores
// --data-dir, which would leave the Debian service unable to decrypt RCON
// passwords it wrote before a restart (or read another instance's).
builder.Services.AddDataProtection()
    .SetApplicationName("CraftConsole")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appDataPath, "dpkeys")));
builder.Services.AddSingleton<RconSecretStore>();

// TLS certificate resolution has to happen before Build() so Kestrel's HTTPS defaults can be
// wired up — but the DI-registered IDataProtectionProvider above only exists after Build().
// A standalone provider pointed at the same key ring sidesteps that; it's the same mechanism
// that already lets the Debian service decrypt RCON secrets across restarts (see above).
if (httpsEnabled)
{
    var standaloneDataProtection = DataProtectionProvider.Create(
        new DirectoryInfo(Path.Combine(appDataPath, "dpkeys")));
    var tlsLogger = new SerilogLoggerFactory(serilog).CreateLogger<TlsCertificateProvider>();
    var tlsCertificateProvider = new TlsCertificateProvider(settingsHolder, standaloneDataProtection, tlsLogger, args);
    await tlsCertificateProvider.InitializeAsync();

    builder.Services.AddSingleton(tlsCertificateProvider);
    builder.WebHost.ConfigureKestrel(o => o.ConfigureHttpsDefaults(
        h => h.ServerCertificateSelector = (_, _) => tlsCertificateProvider.Current));
}

builder.Services.AddSingleton<EventBroker>();
// ServerSupervisor is no longer registered directly — one exists per server,
// owned by this registry. See ServerRegistry's and ServerSupervisor's own
// doc comments for why.
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ProfilesService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<SetupService>();

builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ASP.NET Core's own multipart body limit (128 MB) applies independently of
// Kestrel's request body limit — the upload routes disable the latter, but
// still need this raised too, or a large world upload is rejected before
// WorkspaceApi's own size check ever runs.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = WorkspaceApi.MaxUploadBytes;
});

var app = builder.Build();

await app.Services.GetRequiredService<AuthService>().InitializeAsync();

// ── Auth gate ────────────────────────────────────────────────────────────
// Everything except the three public auth endpoints requires a valid session
// cookie. Unauthenticated API calls get 401; unauthenticated page loads get
// the login/setup page instead of the SPA, so wwwroot's JS never even runs
// unauthenticated. logout/change-password deliberately do NOT go in this set
// — they need a real session, not just knowledge of the current password.
var publicAuthPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/api/auth/status", "/api/auth/setup", "/api/auth/login",
};

app.Use(async (ctx, next) =>
{
    // Every response here depends on session state (SPA shell vs. login page,
    // or live JSON). The static file provider stamps index.html with
    // Last-Modified/ETag, which is enough for a browser to replay it from
    // cache after logout without ever hitting this gate again — block that.
    ctx.Response.Headers.CacheControl = "no-store";

    if (publicAuthPaths.Contains(ctx.Request.Path.Value ?? ""))
    {
        await next();
        return;
    }

    // The login/setup page is rendered by this gate itself, so its own webfonts
    // have to be reachable without a session. Otherwise they fall through to the
    // catch-all below and the browser is handed login HTML in place of a woff2
    // ("OTS parsing error: invalid sfntVersion"), leaving the very first screen
    // a user sees on a fallback system face. Fonts are static and carry no
    // session state, so only GET/HEAD is opened up.
    if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
        && ctx.Request.Path.StartsWithSegments("/fonts"))
    {
        await next();
        return;
    }
    var auth = ctx.RequestServices.GetRequiredService<AuthService>();
    var session = auth.TryValidateSession(ctx.Request.Cookies[AuthApi.CookieName]);
    if (session is not null)
    {
        ctx.Items[HttpContextAuthExtensions.SessionInfoKey] = session;

        // Routing has already matched an endpoint by this point in the pipeline
        // even though this middleware is registered before the Map*Api() calls —
        // WebApplication auto-inserts routing at the true start of the pipeline.
        var required = ctx.GetEndpoint()?.Metadata.GetMetadata<RequiredRole>();
        if (required is not null && session.Role < required.Role)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { Message = "Your role does not allow this." });
            return;
        }

        await next();
        return;
    }

    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(LoginPage.Render(auth.IsConfigured));
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
});

// Development serves wwwroot from disk so edits show up on refresh without a
// rebuild. Published builds serve the copies embedded in the assembly, which is
// what makes the single-file executable genuinely self-contained — otherwise
// PublishSingleFile leaves wwwroot as loose files beside the binary.
// Both UseDefaultFiles and UseStaticFiles need the provider: without it on the
// first, "/" never resolves to index.html.
if (app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
else
{
    var embedded = new ManifestEmbeddedFileProvider(
        typeof(Program).Assembly, "wwwroot");

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });
}

app.MapAuthApi();
app.MapServerApi();
app.MapPlayersApi();
app.MapWorkspaceApi();
app.MapAutomationApi();
app.MapSetupApi();
app.MapSystemApi();
app.MapUsersApi();

if (httpsEnabled)
    app.MapTlsApi();

if (app.Environment.IsDevelopment())
    app.MapDevApi();

// Gracefully stop every running server when the panel shuts down. Resolved
// once here rather than via app.Services inside the callback: on a
// failed-startup path this Stopping callback can fire while the provider is
// already disposing, and GetRequiredService at that point throws
// ObjectDisposedException — harmless (nothing was running to stop) but noisy
// in exactly the shutdown path this exists for. The registry itself stays
// valid to call even after the provider starts disposing; only resolving a
// *new* service from it does not.
var serverRegistry = app.Services.GetRequiredService<ServerRegistry>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    serverRegistry.DisposeAllAsync().GetAwaiter().GetResult();
});

// Open the panel in the default browser. Skipped for dev runs, for --no-browser,
// and under systemd — a service account has no session to open a browser in.
var headless = args.Contains("--no-browser")
    || Environment.GetEnvironmentVariable("INVOCATION_ID") is not null; // set by systemd

if (!headless && !app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? $"{(httpsEnabled ? "https" : "http")}://127.0.0.1:5178";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* headless environment — user opens the URL manually */ }
    });
}

app.Run();

/// <summary>
/// Named entry point so integration tests can host the app through
/// WebApplicationFactory; top-level statements alone generate an internal class.
/// </summary>
public partial class Program;
