using System.Diagnostics;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Localhost only unless the user explicitly overrides via --urls / ASPNETCORE_URLS.
// A password is required for every request once one is set up (see AuthService /
// the auth gate below), so widening the bind is reasonable after setup — but do
// it over a trusted network or a tunnel; there's still no TLS here.
if (builder.Configuration["urls"] is null
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5178");
}

var appDataPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CraftConsole");

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

app.UseDefaultFiles();
app.UseStaticFiles();

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

// Open the panel in the default browser (skippable with --no-browser; dev runs skip it too)
if (!args.Contains("--no-browser") && !app.Environment.IsDevelopment())
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
