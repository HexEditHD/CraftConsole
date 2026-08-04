using System.Net;
using System.Text;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Http;
using Xunit;

namespace CraftConsole.Tests.Setup;

public class ServerDownloadServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<HttpRequestMessage> Requests { get; } = [];

        public StubHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static ServerDownloadService NewService(params string[] bodies)
    {
        var handler = new StubHandler(bodies);
        return new ServerDownloadService(new HttpClient(handler), new DownloadService(new HttpClient(handler)));
    }

    // ── Paper (v3) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Paper_versions_flatten_the_v3_object_shape_newest_first()
    {
        const string body = """
            {
              "versions": {
                "26.2": ["26.2", "26.2-rc-2"],
                "1.21": ["1.21.11", "1.21"]
              }
            }
            """;
        var service = NewService(body);

        var versions = await service.FetchVersionsAsync(ServerType.Paper);

        Assert.Equal(["26.2", "26.2-rc-2", "1.21.11", "1.21"], versions);
    }

    [Fact]
    public async Task Paper_requests_send_a_descriptive_user_agent()
    {
        var handler = new StubHandler("""{"versions":{"26.2":["26.2"]}}""");
        var service = new ServerDownloadService(new HttpClient(handler), new DownloadService(new HttpClient(handler)));

        await service.FetchVersionsAsync(ServerType.Paper);

        Assert.NotEmpty(handler.Requests);
        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task Paper_resolve_reads_the_content_addressed_download_url_verbatim()
    {
        const string buildBody = """
            {
              "downloads": {
                "server:default": {
                  "name": "paper-1.21.9-59.jar",
                  "url": "https://fill-data.papermc.io/v1/objects/abc123/paper-1.21.9-59.jar"
                }
              }
            }
            """;
        var service = NewService(buildBody);

        var (version, url) = await service.ResolveVersionAsync(ServerType.Paper, "1.21.9");

        Assert.Equal("1.21.9", version);
        Assert.Equal("https://fill-data.papermc.io/v1/objects/abc123/paper-1.21.9-59.jar", url);
    }

    [Fact]
    public async Task Paper_resolve_looks_up_the_newest_version_when_none_is_specified()
    {
        const string versionsBody = """
            { "versions": { "26.2": ["26.2"] } }
            """;
        const string buildBody = """
            { "downloads": { "server:default": { "url": "https://fill-data.papermc.io/v1/objects/x/paper-26.2.jar" } } }
            """;
        var service = NewService(versionsBody, buildBody);

        var (version, url) = await service.ResolveVersionAsync(ServerType.Paper);

        Assert.Equal("26.2", version);
        Assert.EndsWith("paper-26.2.jar", url);
    }

    // ── Fabric ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Fabric_versions_include_only_stable_entries()
    {
        const string body = """
            [
              {"version": "26.3-snapshot-6", "stable": false},
              {"version": "26.2", "stable": true},
              {"version": "26.1", "stable": true}
            ]
            """;
        var service = NewService(body);

        var versions = await service.FetchVersionsAsync(ServerType.Fabric);

        Assert.Equal(["26.2", "26.1"], versions);
    }

    [Fact]
    public async Task Fabric_resolve_picks_the_latest_stable_loader_and_installer()
    {
        const string loaderBody = """
            [{"version":"0.19.3","stable":true},{"version":"0.19.2","stable":false}]
            """;
        const string installerBody = """
            [{"version":"1.1.2","stable":true},{"version":"1.1.1","stable":false}]
            """;
        var service = NewService(loaderBody, installerBody);

        var (version, url) = await service.ResolveVersionAsync(ServerType.Fabric, "1.21.9");

        Assert.Equal("1.21.9", version);
        Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.21.9/0.19.3/1.1.2/server/jar", url);
    }

    // ── NeoForge ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NeoForge_versions_are_read_newest_first_from_maven_metadata()
    {
        const string body = """
            <metadata>
              <versioning>
                <versions>
                  <version>20.2.12-beta</version>
                  <version>21.1.240</version>
                  <version>26.2.0.45-beta</version>
                </versions>
              </versioning>
            </metadata>
            """;
        var service = NewService(body);

        var versions = await service.FetchVersionsAsync(ServerType.NeoForge);

        Assert.Equal(["26.2.0.45-beta", "21.1.240", "20.2.12-beta"], versions);
    }

    [Fact]
    public async Task NeoForge_resolve_always_points_at_ServerStarterJar_regardless_of_version()
    {
        var service = NewService(); // explicit version supplied — no HTTP call expected

        var (version, url) = await service.ResolveVersionAsync(ServerType.NeoForge, "26.2.0.45-beta");

        Assert.Equal("26.2.0.45-beta", version);
        Assert.Equal(
            "https://github.com/neoforged/ServerStarterJar/releases/latest/download/server.jar", url);
    }

    [Fact]
    public async Task NeoForge_resolve_uses_the_newest_version_when_none_is_specified()
    {
        const string body = """
            <metadata><versioning><versions>
              <version>21.1.240</version>
              <version>26.2.0.45-beta</version>
            </versions></versioning></metadata>
            """;
        var service = NewService(body);

        var (version, url) = await service.ResolveVersionAsync(ServerType.NeoForge);

        Assert.Equal("26.2.0.45-beta", version);
        Assert.EndsWith("server.jar", url);
    }

    // ── Manual types ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ServerType.Spigot)]
    [InlineData(ServerType.Forge)]
    public async Task Resolving_a_manual_type_throws_NotSupported_without_any_http_call(ServerType type)
    {
        var handler = new StubHandler();
        var service = new ServerDownloadService(new HttpClient(handler), new DownloadService(new HttpClient(handler)));

        await Assert.ThrowsAsync<NotSupportedException>(() => service.ResolveVersionAsync(type));

        Assert.Empty(handler.Requests);
    }
}
