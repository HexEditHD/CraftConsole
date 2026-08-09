using System.Net;
using System.Text;
using CraftConsole.Infrastructure.Http;
using Xunit;

namespace CraftConsole.Tests.Modrinth;

public class ModrinthClientTests
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

    private static ModrinthClient NewClient(out StubHandler handler, params string[] bodies)
    {
        handler = new StubHandler(bodies);
        return new ModrinthClient(new HttpClient(handler));
    }

    private static string QueryParam(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == name) return Uri.UnescapeDataString(kv[1]);
        }
        throw new InvalidOperationException($"Query param '{name}' not found in {uri}");
    }

    // ── Search ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_builds_a_project_type_facet_and_an_OR_loaders_facet()
    {
        var client = NewClient(out var handler, """{"hits":[],"total_hits":0}""");

        await client.SearchAsync("luckperms", "plugin", ["paper", "spigot", "bukkit"], gameVersion: null, offset: 0, limit: 20);

        var facets = QueryParam(handler.Requests[0].RequestUri!, "facets");
        Assert.Equal("""[["project_type:plugin"],["categories:paper","categories:spigot","categories:bukkit"]]""", facets);
    }

    [Fact]
    public async Task Search_adds_a_versions_facet_only_when_a_game_version_is_given()
    {
        var client = NewClient(out var handler, """{"hits":[],"total_hits":0}""", """{"hits":[],"total_hits":0}""");

        await client.SearchAsync("q", "mod", ["fabric"], gameVersion: "1.21.4", offset: 0, limit: 20);
        await client.SearchAsync("q", "mod", ["fabric"], gameVersion: null, offset: 0, limit: 20);

        Assert.Equal(
            """[["project_type:mod"],["categories:fabric"],["versions:1.21.4"]]""",
            QueryParam(handler.Requests[0].RequestUri!, "facets"));
        Assert.Equal(
            """[["project_type:mod"],["categories:fabric"]]""",
            QueryParam(handler.Requests[1].RequestUri!, "facets"));
    }

    [Fact]
    public async Task Search_omits_the_loaders_facet_when_no_loaders_are_given()
    {
        var client = NewClient(out var handler, """{"hits":[],"total_hits":0}""");

        await client.SearchAsync("q", "plugin", [], gameVersion: null, offset: 0, limit: 20);

        Assert.Equal("""[["project_type:plugin"]]""", QueryParam(handler.Requests[0].RequestUri!, "facets"));
    }

    [Fact]
    public async Task Search_parses_hits_and_falls_back_for_missing_optional_fields()
    {
        const string body = """
            {
              "hits": [
                {
                  "project_id": "abc123",
                  "slug": "myplugin",
                  "title": "My Plugin",
                  "description": "Does things",
                  "author": "someone",
                  "project_type": "plugin"
                }
              ],
              "total_hits": 1
            }
            """;
        var client = NewClient(out _, body);

        var result = await client.SearchAsync("q", "plugin", [], null, 0, 20);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("abc123", hit.ProjectId);
        Assert.Equal("My Plugin", hit.Title);
        Assert.Null(hit.IconUrl);   // absent in the response
        Assert.Equal(0, hit.Downloads); // absent in the response
        Assert.Equal(1, result.TotalHits);
    }

    [Fact]
    public async Task Search_sends_a_descriptive_user_agent()
    {
        var client = NewClient(out var handler, """{"hits":[],"total_hits":0}""");

        await client.SearchAsync("q", "plugin", [], null, 0, 20);

        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    // ── Version listing ──────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectVersions_includes_loaders_and_game_version_query_params_when_given()
    {
        var client = NewClient(out var handler, "[]");

        await client.GetProjectVersionsAsync("abc123", ["paper", "spigot"], "1.21.4");

        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal("""["paper","spigot"]""", QueryParam(uri, "loaders"));
        Assert.Equal("""["1.21.4"]""", QueryParam(uri, "game_versions"));
    }

    [Fact]
    public async Task GetProjectVersions_omits_query_params_when_none_are_given()
    {
        var client = NewClient(out var handler, "[]");

        await client.GetProjectVersionsAsync("abc123", [], null);

        Assert.Equal("", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetProjectVersions_parses_files_dependencies_loaders_and_game_versions()
    {
        const string body = """
            [
              {
                "id": "v1",
                "project_id": "abc123",
                "name": "Version 1",
                "version_number": "1.0.0",
                "game_versions": ["1.21.4", "1.21.5"],
                "loaders": ["paper"],
                "dependencies": [
                  {"version_id": "dep-v1", "project_id": null, "file_name": null, "dependency_type": "required"},
                  {"version_id": null, "project_id": "dep-proj", "file_name": null, "dependency_type": "optional"}
                ],
                "files": [
                  {"url": "https://cdn.example/file.jar", "filename": "file.jar", "primary": true, "size": 1024}
                ]
              }
            ]
            """;
        var client = NewClient(out _, body);

        var versions = await client.GetProjectVersionsAsync("abc123", [], null);

        var v = Assert.Single(versions);
        Assert.Equal("v1", v.Id);
        Assert.Equal("1.0.0", v.VersionNumber);
        Assert.Equal(["1.21.4", "1.21.5"], v.GameVersions);
        Assert.Equal(["paper"], v.Loaders);

        Assert.Equal(2, v.Dependencies.Count);
        Assert.Equal("dep-v1", v.Dependencies[0].VersionId);
        Assert.Null(v.Dependencies[0].ProjectId);
        Assert.Equal("required", v.Dependencies[0].DependencyType);
        Assert.Equal("dep-proj", v.Dependencies[1].ProjectId);
        Assert.Equal("optional", v.Dependencies[1].DependencyType);

        var file = Assert.Single(v.Files);
        Assert.Equal("https://cdn.example/file.jar", file.Url);
        Assert.Equal("file.jar", file.FileName);
        Assert.True(file.Primary);
        Assert.Equal(1024, file.Size);
    }

    [Fact]
    public async Task GetProjectVersions_treats_a_missing_dependencies_property_as_no_dependencies()
    {
        const string body = """
            [{"id":"v2","project_id":"abc123","name":"n","version_number":"1.0","game_versions":[],"loaders":[],
              "files":[{"url":"https://x/file.jar","filename":"file.jar","primary":true,"size":10}]}]
            """;
        var client = NewClient(out _, body);

        var versions = await client.GetProjectVersionsAsync("abc123", [], null);

        Assert.Empty(Assert.Single(versions).Dependencies);
    }

    [Fact]
    public async Task GetVersion_parses_a_single_version_the_same_way_as_the_list_endpoint()
    {
        const string body = """
            {"id":"v1","project_id":"abc123","name":"n","version_number":"1.0",
             "game_versions":["1.21.4"],"loaders":["fabric"],"dependencies":[],
             "files":[{"url":"https://x/file.jar","filename":"file.jar","primary":true,"size":10}]}
            """;
        var client = NewClient(out var handler, body);

        var version = await client.GetVersionAsync("v1");

        Assert.Equal("v1", version.Id);
        Assert.Equal("abc123", version.ProjectId);
        Assert.EndsWith("/version/v1", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetProjectTitle_reads_the_title_field()
    {
        var client = NewClient(out var handler, """{"title":"My Plugin"}""");

        var title = await client.GetProjectTitleAsync("abc123");

        Assert.Equal("My Plugin", title);
        Assert.EndsWith("/project/abc123", handler.Requests[0].RequestUri!.AbsolutePath);
    }
}
