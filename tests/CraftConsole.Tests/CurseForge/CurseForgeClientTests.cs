using System.Net;
using System.Text;
using CraftConsole.Infrastructure.Http;
using Xunit;

namespace CraftConsole.Tests.CurseForge;

public class CurseForgeClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = [];

        public StubHandler Json(string body)
        {
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        public StubHandler Status(HttpStatusCode code)
        {
            _responses.Enqueue(() => new HttpResponseMessage(code));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private static CurseForgeClient NewClient(out StubHandler handler)
    {
        handler = new StubHandler();
        return new CurseForgeClient(new HttpClient(handler));
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
    public async Task Search_includes_gameId_classId_query_and_paging()
    {
        var client = NewClient(out var handler);
        handler.Json("""{"data":[],"pagination":{"totalCount":0}}""");

        await client.SearchAsync("apikey", "luckperms", classId: 5, modLoaderType: null, gameVersion: null, offset: 10, limit: 20);

        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal("432", QueryParam(uri, "gameId"));
        Assert.Equal("5", QueryParam(uri, "classId"));
        Assert.Equal("luckperms", QueryParam(uri, "searchFilter"));
        Assert.Equal("10", QueryParam(uri, "index"));
        Assert.Equal("20", QueryParam(uri, "pageSize"));
    }

    [Fact]
    public async Task Search_includes_modLoaderType_and_gameVersion_only_when_given()
    {
        var client = NewClient(out var handler);
        handler.Json("""{"data":[],"pagination":{"totalCount":0}}""").Json("""{"data":[],"pagination":{"totalCount":0}}""");

        await client.SearchAsync("apikey", "q", 6, modLoaderType: 4, gameVersion: "1.21.4", offset: 0, limit: 20);
        await client.SearchAsync("apikey", "q", 6, modLoaderType: null, gameVersion: null, offset: 0, limit: 20);

        var withParams = handler.Requests[0].RequestUri!;
        Assert.Equal("4", QueryParam(withParams, "modLoaderType"));
        Assert.Equal("1.21.4", QueryParam(withParams, "gameVersion"));

        var withoutParams = handler.Requests[1].RequestUri!.Query;
        Assert.DoesNotContain("modLoaderType", withoutParams);
        Assert.DoesNotContain("gameVersion", withoutParams);
    }

    [Fact]
    public async Task Search_sends_the_api_key_and_a_descriptive_user_agent()
    {
        var client = NewClient(out var handler);
        handler.Json("""{"data":[],"pagination":{"totalCount":0}}""");

        await client.SearchAsync("my-secret-key", "q", 6, null, null, 0, 20);

        Assert.Equal("my-secret-key", Assert.Single(handler.Requests[0].Headers.GetValues("x-api-key")));
        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task Search_parses_hits_and_falls_back_for_missing_optional_fields()
    {
        const string body = """
            {
              "data": [
                {
                  "id": 12345,
                  "name": "My Plugin",
                  "authors": []
                }
              ],
              "pagination": { "totalCount": 1 }
            }
            """;
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var result = await client.SearchAsync("key", "q", 6, null, null, 0, 20);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(12345, hit.ModId);
        Assert.Equal("My Plugin", hit.Name);
        Assert.Equal("", hit.Slug);      // absent in the response
        Assert.Equal("", hit.Author);    // authors is empty
        Assert.Null(hit.IconUrl);        // absent in the response
        Assert.Equal(0, hit.Downloads);  // absent in the response
        Assert.Equal(1, result.TotalHits);
    }

    [Fact]
    public async Task Search_throws_a_clear_error_when_the_api_key_is_rejected()
    {
        var client = NewClient(out var handler);
        handler.Status(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SearchAsync("bad-key", "q", 6, null, null, 0, 20));

        Assert.Contains("API key", ex.Message);
    }

    // ── Mod files ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModFiles_parses_files_and_maps_relationType_to_the_shared_vocabulary()
    {
        const string body = """
            {
              "data": [
                {
                  "id": 999,
                  "modId": 12345,
                  "fileName": "plugin.jar",
                  "downloadUrl": "https://cdn.example/plugin.jar",
                  "fileLength": 2048,
                  "gameVersions": ["1.21.4"],
                  "dependencies": [
                    {"modId": 1, "relationType": 3},
                    {"modId": 2, "relationType": 2},
                    {"modId": 3, "relationType": 5},
                    {"modId": 4, "relationType": 1},
                    {"modId": 5, "relationType": 6},
                    {"modId": 6, "relationType": 99}
                  ]
                }
              ]
            }
            """;
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var files = await client.GetModFilesAsync("key", 12345, null, null);

        var file = Assert.Single(files);
        Assert.Equal(999, file.Id);
        Assert.Equal("plugin.jar", file.FileName);
        Assert.Equal("https://cdn.example/plugin.jar", file.DownloadUrl);
        Assert.Equal(2048, file.FileLength);
        Assert.Equal(["1.21.4"], file.GameVersions);

        Assert.Equal(["required", "optional", "incompatible", "embedded", "embedded", "optional"],
            file.Dependencies.Select(d => d.RelationType));
    }

    [Fact]
    public async Task GetModFiles_parses_display_name_release_type_and_file_date()
    {
        const string body = """
            {"data":[{"id":999,"modId":12345,"fileName":"plugin-1.0.jar","displayName":"Plugin (1.0 Beta)",
              "fileLength":10,"gameVersions":[],"releaseType":2,"fileDate":"2024-06-01T12:00:00Z"}]}
            """;
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var file = Assert.Single(await client.GetModFilesAsync("key", 12345, null, null));

        Assert.Equal("Plugin (1.0 Beta)", file.DisplayName);
        Assert.Equal("beta", file.ReleaseType);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero), file.FileDate);
    }

    [Fact]
    public async Task GetModFiles_falls_back_to_the_file_name_when_no_display_name_is_given()
    {
        const string body = """{"data":[{"id":999,"modId":12345,"fileName":"plugin.jar","fileLength":10,"gameVersions":[]}]}""";
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var file = Assert.Single(await client.GetModFilesAsync("key", 12345, null, null));

        Assert.Equal("plugin.jar", file.DisplayName);
        Assert.Equal("", file.ReleaseType);
        Assert.Null(file.FileDate);
    }

    [Fact]
    public async Task GetModFiles_treats_a_missing_dependencies_property_as_no_dependencies()
    {
        const string body = """
            {"data":[{"id":1,"modId":2,"fileName":"x.jar","fileLength":1,"gameVersions":[]}]}
            """;
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var files = await client.GetModFilesAsync("key", 2, null, null);

        Assert.Empty(Assert.Single(files).Dependencies);
    }

    [Fact]
    public async Task GetModFiles_includes_modLoaderType_and_gameVersion_only_when_given()
    {
        var handler = new StubHandler().Json("""{"data":[]}""").Json("""{"data":[]}""");
        var client = new CurseForgeClient(new HttpClient(handler));

        await client.GetModFilesAsync("key", 5, modLoaderType: 1, gameVersion: "1.21.4");
        await client.GetModFilesAsync("key", 5, modLoaderType: null, gameVersion: null);

        Assert.Equal("1", QueryParam(handler.Requests[0].RequestUri!, "modLoaderType"));
        Assert.Equal("", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task GetFile_parses_a_single_file_the_same_way_as_the_list_endpoint()
    {
        const string body = """{"data":{"id":999,"modId":12345,"fileName":"plugin.jar","fileLength":10,"gameVersions":[]}}""";
        var handler = new StubHandler().Json(body);
        var client = new CurseForgeClient(new HttpClient(handler));

        var file = await client.GetFileAsync("key", 12345, 999);

        Assert.Equal(999, file.Id);
        Assert.Equal(12345, file.ModId);
        Assert.EndsWith("/mods/12345/files/999", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetModName_reads_the_name_field()
    {
        var handler = new StubHandler().Json("""{"data":{"name":"My Plugin"}}""");
        var client = new CurseForgeClient(new HttpClient(handler));

        var name = await client.GetModNameAsync("key", 12345);

        Assert.Equal("My Plugin", name);
        Assert.EndsWith("/mods/12345", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    // ── Download URL fallback ────────────────────────────────────────────

    [Fact]
    public async Task ResolveDownloadUrl_returns_the_url_when_present()
    {
        var handler = new StubHandler().Json("""{"data":"https://cdn.example/plugin.jar"}""");
        var client = new CurseForgeClient(new HttpClient(handler));

        var url = await client.ResolveDownloadUrlAsync("key", 12345, 999);

        Assert.Equal("https://cdn.example/plugin.jar", url);
    }

    [Fact]
    public async Task ResolveDownloadUrl_returns_null_when_third_party_downloads_are_disabled()
    {
        // CurseForge's own behaviour for this case: 200 OK with no usable data,
        // not an error status — there's nothing left to try after this.
        var handler = new StubHandler().Json("""{"data":null}""");
        var client = new CurseForgeClient(new HttpClient(handler));

        var url = await client.ResolveDownloadUrlAsync("key", 12345, 999);

        Assert.Null(url);
    }
}
