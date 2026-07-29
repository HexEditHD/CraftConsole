using System.Diagnostics;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Infrastructure.Logging;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.Extensions.FileProviders;
using Serilog;

// The configuration binder reads "--key value" as a pair, so a valueless flag
// swallows whatever follows it: "--no-browser --urls http://…" bound the default
// address because --urls became the *value* of --no-browser. Flags CraftConsole
// interprets itself are removed before the host ever sees them.
string[] ownFlags = ["--no-browser"];
var hostArgs = args.Where(a => !ownFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

// Localhost only unless the user explicitly overrides via --urls / ASPNETCORE_URLS.
// A password is required for every request once one is set up (see AuthService /
// the auth gate below), so widening the bind is reasonable after setup — but do
// it over a trusted network or a tunnel; there's still no TLS here.
if (builder.Configuration["urls"] is null
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5178");
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

builder.Services.AddSingleton(new SettingsHolder(appDataPath));
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<ServerDownloadService>();
builder.Services.AddSingleton<JavaDownloadService>();

builder.Services.AddSingleton<EventBroker>();
builder.Services.AddSingleton<ServerSupervisor>();
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

    var auth = ctx.RequestServices.GetRequiredService<AuthService>();
    if (auth.TryValidateSession(ctx.Request.Cookies[AuthApi.CookieName]))
    {
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

if (app.Environment.IsDevelopment())
    app.MapDevApi();

// Gracefully stop a running server when the panel shuts down
app.Lifetime.ApplicationStopping.Register(() =>
{
    var supervisor = app.Services.GetRequiredService<ServerSupervisor>();
    supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

// Open the panel in the default browser. Skipped for dev runs, for --no-browser,
// and under systemd — a service account has no session to open a browser in.
var headless = args.Contains("--no-browser")
    || Environment.GetEnvironmentVariable("INVOCATION_ID") is not null; // set by systemd

if (!headless && !app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5178";
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
