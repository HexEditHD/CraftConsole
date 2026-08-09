using System.Net;
using System.Text;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Tests.Servers;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CraftConsole.Tests.CurseForge;

/// <summary>See ModrinthServiceTests' own doc comment for why this shares FakeServerCollection.</summary>
[Collection(nameof(FakeServerCollection))]
public class CurseForgeServiceTests
{
    // ── HTTP stub ────────────────────────────────────────────────────────
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<Uri> RequestUris { get; } = [];

        public StubHandler Json(string body)
        {
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        public StubHandler Bytes(string content)
        {
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue()());
        }
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

    private static async Task<CurseForgeSecretStore> NewApiKeyStoreAsync(SettingsHolder settings, string? apiKey)
    {
        var store = new CurseForgeSecretStore(
            settings,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(settings.AppDataPath, "dpkeys"))),
            NullLogger<CurseForgeSecretStore>.Instance);
        if (apiKey is not null) await store.SetAsync(apiKey);
        return store;
    }

    private static CurseForgeService NewService(StubHandler handler, CurseForgeSecretStore apiKey, SettingsHolder settings)
    {
        var http = new HttpClient(handler);
        return new CurseForgeService(new CurseForgeClient(http), new DownloadService(http), apiKey, settings);
    }

    // ── A started supervisor, backed by the fake server ─────────────────
    // Same reasoning as ModrinthServiceTests.StartedSupervisor.
    private sealed class StartedSupervisor : IAsyncDisposable
    {
        public ServerSupervisor Supervisor { get; }
        public SettingsHolder Settings { get; }
        public CurseForgeSecretStore ApiKey { get; }
        public string WorkingDirectory { get; }

        private StartedSupervisor(ServerSupervisor sup, SettingsHolder settings, CurseForgeSecretStore apiKey, string dir)
        {
            Supervisor = sup;
            Settings = settings;
            ApiKey = apiKey;
            WorkingDirectory = dir;
        }

        public static async Task<StartedSupervisor> CreateAsync(ServerType type, string minecraftVersion = "", string? apiKey = "test-key")
        {
            var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);

            var settings = new SettingsHolder(dir);
            var secrets = new RconSecretStore(
                settings,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "dpkeys"))),
                NullLogger<RconSecretStore>.Instance);

            var sup = new ServerSupervisor(
                Guid.NewGuid(), new EventBroker(), settings, new HttpClient(), NullLogger<ServerSupervisor>.Instance, secrets);

            var profile = FakeServer.Profile(dir);
            profile.Type = type;
            profile.MinecraftVersion = minecraftVersion;
            await sup.StartAsync(profile);

            var keyStore = await NewApiKeyStoreAsync(settings, apiKey);
            return new StartedSupervisor(sup, settings, keyStore, dir);
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            try { Directory.Delete(WorkingDirectory, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── ServerType → CurseForge class/loader ─────────────────────────────

    [Theory]
    [InlineData(ServerType.Paper, "5", null)]
    [InlineData(ServerType.Purpur, "5", null)]
    [InlineData(ServerType.Spigot, "5", null)]
    [InlineData(ServerType.Fabric, "6", "4")]
    [InlineData(ServerType.Forge, "6", "1")]
    [InlineData(ServerType.NeoForge, "6", "6")]
    public async Task Search_maps_each_server_type_to_its_CurseForge_class_and_loader(
        ServerType type, string expectedClassId, string? expectedModLoaderType)
    {
        var handler = new StubHandler().Json("""{"data":[],"pagination":{"totalCount":0}}""");
        var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-settings-" + Guid.NewGuid());
        var settings = new SettingsHolder(dir);
        var apiKey = await NewApiKeyStoreAsync(settings, "test-key");
        var service = NewService(handler, apiKey, settings);
        var profile = new ServerProfile { Name = "x", Type = type, MinecraftVersion = "" };

        await service.SearchAsync(profile, "query", 0, 20, CancellationToken.None);

        var uri = handler.RequestUris[0];
        Assert.Equal(expectedClassId, QueryParam(uri, "classId"));
        if (expectedModLoaderType is null)
            Assert.DoesNotContain("modLoaderType", uri.Query);
        else
            Assert.Equal(expectedModLoaderType, QueryParam(uri, "modLoaderType"));
    }

    [Fact]
    public async Task Search_returns_nothing_for_Vanilla_without_making_a_request()
    {
        var handler = new StubHandler(); // no responses queued — a call would throw
        var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-settings-" + Guid.NewGuid());
        var settings = new SettingsHolder(dir);
        var apiKey = await NewApiKeyStoreAsync(settings, "test-key");
        var service = NewService(handler, apiKey, settings);
        var profile = new ServerProfile { Name = "x", Type = ServerType.Vanilla };

        var result = await service.SearchAsync(profile, "query", 0, 20, CancellationToken.None);

        Assert.Empty(result.Hits);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Search_throws_when_no_api_key_is_configured()
    {
        var handler = new StubHandler(); // no responses queued — a call would throw
        var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-settings-" + Guid.NewGuid());
        var settings = new SettingsHolder(dir);
        var apiKey = await NewApiKeyStoreAsync(settings, apiKey: null);
        var service = NewService(handler, apiKey, settings);
        var profile = new ServerProfile { Name = "x", Type = ServerType.Paper };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchAsync(profile, "query", 0, 20, CancellationToken.None));

        Assert.Contains("API key", ex.Message);
        Assert.Empty(handler.RequestUris);
    }

    // ── Guard clauses that don't need a started supervisor ───────────────

    [Fact]
    public async Task Install_throws_when_the_server_has_never_been_started()
    {
        var handler = new StubHandler();
        var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-settings-" + Guid.NewGuid());
        var settings = new SettingsHolder(dir);
        var apiKey = await NewApiKeyStoreAsync(settings, "test-key");
        var service = NewService(handler, apiKey, settings);
        var sup = new ServerSupervisor(
            Guid.NewGuid(), new EventBroker(), settings, new HttpClient(),
            NullLogger<ServerSupervisor>.Instance,
            new RconSecretStore(settings, DataProtectionProvider.Create(new DirectoryInfo(dir)), NullLogger<RconSecretStore>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(sup, modId: 1, fileId: 1, includeDependencies: false, CancellationToken.None));

        Assert.Equal("No server has been started yet.", ex.Message);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Remove_returns_false_for_a_mod_that_was_never_tracked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-curseforge-settings-" + Guid.NewGuid());
        var settings = new SettingsHolder(dir);
        var apiKey = await NewApiKeyStoreAsync(settings, "test-key");
        var service = NewService(new StubHandler(), apiKey, settings);
        var sup = new ServerSupervisor(
            Guid.NewGuid(), new EventBroker(), settings, new HttpClient(),
            NullLogger<ServerSupervisor>.Instance,
            new RconSecretStore(settings, DataProtectionProvider.Create(new DirectoryInfo(dir)), NullLogger<RconSecretStore>.Instance));

        Assert.False(await service.RemoveAsync(sup, modId: 999));
    }

    // ── Install / dependency confirmation / remove, against a real profile ─

    [Fact]
    public async Task Install_rejects_a_Vanilla_profile_which_has_no_plugin_or_mod_system()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Vanilla);
        var service = NewService(new StubHandler(), started.ApiKey, started.Settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(started.Supervisor, 1, 1, includeDependencies: false, CancellationToken.None));

        Assert.Contains("Vanilla", ex.Message);
        Assert.Contains("no plugin or mod system", ex.Message);
    }

    [Fact]
    public async Task Install_with_no_dependencies_writes_the_jar_into_plugins_and_tracks_it()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string fileBody = """
            {"data":{"id":999,"modId":12345,"fileName":"plugin.jar","downloadUrl":"https://cdn.example/plugin.jar","fileLength":5,"gameVersions":["1.21.4"]}}
            """;
        var handler = new StubHandler().Json(fileBody).Bytes("hello").Json("""{"data":{"name":"My Plugin"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None);

        Assert.False(result.NeedsDependencyConfirmation);
        var installed = Assert.Single(result.Installed);
        Assert.Equal(12345, installed.ModId);
        Assert.Equal("My Plugin", installed.ModName);
        Assert.Equal("plugin.jar", installed.FileName);

        var jarPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar");
        Assert.True(File.Exists(jarPath));
        Assert.Equal("hello", await File.ReadAllTextAsync(jarPath));

        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal(12345, listed.ModId);
    }

    [Fact]
    public async Task Install_writes_a_mod_loaders_jar_into_mods_not_plugins()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Fabric);
        const string fileBody = """
            {"data":{"id":999,"modId":12345,"fileName":"mymod.jar","downloadUrl":"https://cdn.example/mymod.jar","fileLength":5,"gameVersions":[]}}
            """;
        var handler = new StubHandler().Json(fileBody).Bytes("hello").Json("""{"data":{"name":"My Mod"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        await service.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(started.WorkingDirectory, "mods", "mymod.jar")));
        Assert.False(Directory.Exists(Path.Combine(started.WorkingDirectory, "plugins")));
    }

    [Fact]
    public async Task Install_falls_back_to_the_download_url_endpoint_when_the_file_has_none()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        // downloadUrl omitted entirely — the author disabled third-party
        // downloads for this file's inline field, forcing the fallback lookup.
        const string fileBody = """{"data":{"id":999,"modId":12345,"fileName":"plugin.jar","fileLength":5,"gameVersions":[]}}""";
        var handler = new StubHandler()
            .Json(fileBody)
            .Json("""{"data":"https://cdn.example/resolved.jar"}""")
            .Bytes("hello")
            .Json("""{"data":{"name":"My Plugin"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None);

        Assert.Single(result.Installed);
        Assert.True(File.Exists(Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar")));
    }

    [Fact]
    public async Task Install_throws_a_clear_error_when_no_download_url_can_be_resolved()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string fileBody = """{"data":{"id":999,"modId":12345,"fileName":"plugin.jar","fileLength":5,"gameVersions":[]}}""";
        var handler = new StubHandler().Json(fileBody).Json("""{"data":null}"""); // fallback also empty
        var service = NewService(handler, started.ApiKey, started.Settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None));

        Assert.Contains("third-party downloads", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(started.WorkingDirectory, "plugins")));
    }

    [Fact]
    public async Task Install_needs_confirmation_for_a_required_dependency_and_installs_nothing_yet()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string mainFileBody = """
            {"data":{"id":999,"modId":100,"fileName":"main.jar","downloadUrl":"https://cdn.example/main.jar","fileLength":5,"gameVersions":[],
             "dependencies":[{"modId":200,"relationType":3}]}}
            """;
        var handler = new StubHandler().Json(mainFileBody).Json("""{"data":{"name":"Dependency Mod"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, 100, 999, includeDependencies: false, CancellationToken.None);

        Assert.True(result.NeedsDependencyConfirmation);
        var dep = Assert.Single(result.RequiredDependencies);
        Assert.Equal(200, dep.ModId);
        Assert.Equal("Dependency Mod", dep.ModName);
        Assert.Empty(result.Installed);

        Assert.False(Directory.Exists(Path.Combine(started.WorkingDirectory, "plugins")));
        Assert.Empty(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Install_with_dependencies_confirmed_installs_both_the_file_and_its_dependency()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string mainFileBody = """
            {"data":{"id":999,"modId":100,"fileName":"main.jar","downloadUrl":"https://cdn.example/main.jar","fileLength":5,"gameVersions":[],
             "dependencies":[{"modId":200,"relationType":3}]}}
            """;
        const string depFilesBody = """
            {"data":[{"id":888,"modId":200,"fileName":"dep.jar","downloadUrl":"https://cdn.example/dep.jar","fileLength":3,"gameVersions":[]}]}
            """;
        var handler = new StubHandler()
            .Json(mainFileBody)
            .Bytes("main-bytes")
            .Json("""{"data":{"name":"Main Plugin"}}""")
            .Json(depFilesBody)
            .Bytes("dep-bytes")
            .Json("""{"data":{"name":"Dependency Mod"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, 100, 999, includeDependencies: true, CancellationToken.None);

        Assert.False(result.NeedsDependencyConfirmation);
        Assert.Equal(2, result.Installed.Count);
        Assert.Contains(result.Installed, i => i.ModId == 100);
        Assert.Contains(result.Installed, i => i.ModId == 200);

        Assert.Equal("main-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "main.jar")));
        Assert.Equal("dep-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "dep.jar")));
        Assert.Equal(2, (await service.ListInstalledAsync(started.Supervisor)).Count);
    }

    [Fact]
    public async Task Reinstalling_the_same_mod_replaces_its_tracking_entry_rather_than_duplicating_it()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        string FileBody(int fileId) => $$$"""
            {"data":{"id":{{{fileId}}},"modId":100,"fileName":"plugin.jar","downloadUrl":"https://cdn.example/plugin.jar","fileLength":5,"gameVersions":[]}}
            """;
        var handler = new StubHandler()
            .Json(FileBody(1)).Bytes("v1-bytes").Json("""{"data":{"name":"My Plugin"}}""")
            .Json(FileBody(2)).Bytes("v2-bytes").Json("""{"data":{"name":"My Plugin"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);

        await service.InstallAsync(started.Supervisor, 100, 1, includeDependencies: false, CancellationToken.None);
        await service.InstallAsync(started.Supervisor, 100, 2, includeDependencies: false, CancellationToken.None);

        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal(2, listed.FileId);
        Assert.Equal("v2-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar")));
    }

    [Fact]
    public async Task Remove_deletes_the_file_and_forgets_the_install()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string fileBody = """
            {"data":{"id":999,"modId":12345,"fileName":"plugin.jar","downloadUrl":"https://cdn.example/plugin.jar","fileLength":5,"gameVersions":[]}}
            """;
        var handler = new StubHandler().Json(fileBody).Bytes("hello").Json("""{"data":{"name":"My Plugin"}}""");
        var service = NewService(handler, started.ApiKey, started.Settings);
        await service.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None);
        var jarPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar");
        Assert.True(File.Exists(jarPath));

        var removed = await service.RemoveAsync(started.Supervisor, 12345);

        Assert.True(removed);
        Assert.False(File.Exists(jarPath));
        Assert.Empty(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Installed_tracking_round_trips_through_disk_for_a_new_service_instance()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string fileBody = """
            {"data":{"id":999,"modId":12345,"fileName":"plugin.jar","downloadUrl":"https://cdn.example/plugin.jar","fileLength":5,"gameVersions":[]}}
            """;
        var writer = NewService(new StubHandler().Json(fileBody).Bytes("hello").Json("""{"data":{"name":"My Plugin"}}"""), started.ApiKey, started.Settings);
        await writer.InstallAsync(started.Supervisor, 12345, 999, includeDependencies: false, CancellationToken.None);

        // A fresh CurseForgeService, same AppDataPath, no HTTP calls made — this
        // only exercises curseforge-installs.json, not the write path above.
        var reader = NewService(new StubHandler(), started.ApiKey, started.Settings);

        var listed = Assert.Single(await reader.ListInstalledAsync(started.Supervisor));
        Assert.Equal(12345, listed.ModId);
    }
}
