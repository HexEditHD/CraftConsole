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

namespace CraftConsole.Tests.Modrinth;

/// <summary>
/// FakeServer-backed tests share the collection ServerProcessManagerTests already
/// serializes on — both spawn a real (fake) child process, and running them
/// concurrently with each other would be needless contention for no benefit.
/// </summary>
[Collection(nameof(FakeServerCollection))]
public class ModrinthServiceTests
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

        public StubHandler Status(HttpStatusCode code)
        {
            _responses.Enqueue(() => new HttpResponseMessage(code));
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

    private static ModrinthService NewService(StubHandler handler, SettingsHolder settings)
    {
        var http = new HttpClient(handler);
        return new ModrinthService(new ModrinthClient(http), new DownloadService(http), settings);
    }

    // ── A started supervisor, backed by the fake server ─────────────────
    // ActiveProfile is only ever set inside ServerSupervisor.StartAsync (never
    // by a test directly — it has no public setter), so tests that need a
    // populated working directory/type genuinely have to start one, same as
    // ServerProcessManagerTests already does elsewhere in this suite.
    private sealed class StartedSupervisor : IAsyncDisposable
    {
        public ServerSupervisor Supervisor { get; }
        public SettingsHolder Settings { get; }
        public string WorkingDirectory { get; }

        private StartedSupervisor(ServerSupervisor sup, SettingsHolder settings, string dir)
        {
            Supervisor = sup;
            Settings = settings;
            WorkingDirectory = dir;
        }

        public static async Task<StartedSupervisor> CreateAsync(ServerType type, string minecraftVersion = "")
        {
            var dir = Path.Combine(Path.GetTempPath(), "cc-modrinth-test-" + Guid.NewGuid());
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

            return new StartedSupervisor(sup, settings, dir);
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            try { Directory.Delete(WorkingDirectory, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── ServerType → Modrinth facets ─────────────────────────────────────

    [Theory]
    [InlineData(ServerType.Paper, "plugin", "paper,spigot,bukkit")]
    [InlineData(ServerType.Purpur, "plugin", "purpur,paper,spigot,bukkit")]
    [InlineData(ServerType.Spigot, "plugin", "spigot,bukkit")]
    [InlineData(ServerType.Fabric, "mod", "fabric")]
    [InlineData(ServerType.Forge, "mod", "forge")]
    [InlineData(ServerType.NeoForge, "mod", "neoforge")]
    public async Task Search_maps_each_server_type_to_its_Modrinth_project_type_and_loaders(
        ServerType type, string expectedProjectType, string expectedLoadersCsv)
    {
        var handler = new StubHandler().Json("""{"hits":[],"total_hits":0}""");
        var dir = Path.Combine(Path.GetTempPath(), "cc-modrinth-settings-" + Guid.NewGuid());
        var service = NewService(handler, new SettingsHolder(dir));
        var profile = new ServerProfile { Name = "x", Type = type, MinecraftVersion = "" };

        await service.SearchAsync(profile, "query", 0, 20, CancellationToken.None);

        var facets = QueryParam(handler.RequestUris[0], "facets");
        var expectedLoaderFacets = string.Join(",", expectedLoadersCsv.Split(',').Select(l => $"\"categories:{l}\""));
        Assert.Equal($"""[["project_type:{expectedProjectType}"],[{expectedLoaderFacets}]]""", facets);
    }

    [Fact]
    public async Task Search_returns_nothing_for_Vanilla_without_making_a_request()
    {
        var handler = new StubHandler(); // no responses queued — a call would throw
        var dir = Path.Combine(Path.GetTempPath(), "cc-modrinth-settings-" + Guid.NewGuid());
        var service = NewService(handler, new SettingsHolder(dir));
        var profile = new ServerProfile { Name = "x", Type = ServerType.Vanilla };

        var result = await service.SearchAsync(profile, "query", 0, 20, CancellationToken.None);

        Assert.Empty(result.Hits);
        Assert.Empty(handler.RequestUris);
    }

    // ── Guard clauses that don't need a started supervisor ───────────────

    [Fact]
    public async Task Install_throws_when_the_server_has_never_been_started()
    {
        var handler = new StubHandler();
        var dir = Path.Combine(Path.GetTempPath(), "cc-modrinth-settings-" + Guid.NewGuid());
        var service = NewService(handler, new SettingsHolder(dir));
        var sup = new ServerSupervisor(
            Guid.NewGuid(), new EventBroker(), new SettingsHolder(dir), new HttpClient(),
            NullLogger<ServerSupervisor>.Instance,
            new RconSecretStore(new SettingsHolder(dir), DataProtectionProvider.Create(new DirectoryInfo(dir)), NullLogger<RconSecretStore>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(sup, "v1", includeDependencies: false, CancellationToken.None));

        Assert.Equal("No server has been started yet.", ex.Message);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Remove_returns_false_for_a_project_that_was_never_tracked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-modrinth-settings-" + Guid.NewGuid());
        var service = NewService(new StubHandler(), new SettingsHolder(dir));
        var sup = new ServerSupervisor(
            Guid.NewGuid(), new EventBroker(), new SettingsHolder(dir), new HttpClient(),
            NullLogger<ServerSupervisor>.Instance,
            new RconSecretStore(new SettingsHolder(dir), DataProtectionProvider.Create(new DirectoryInfo(dir)), NullLogger<RconSecretStore>.Instance));

        Assert.False(await service.RemoveAsync(sup, "unknown-project"));
    }

    // ── Install / dependency confirmation / remove, against a real profile ─

    [Fact]
    public async Task Install_rejects_a_Vanilla_profile_which_has_no_plugin_or_mod_system()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Vanilla);
        var service = NewService(new StubHandler(), started.Settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None));

        Assert.Contains("Vanilla", ex.Message);
        Assert.Contains("no plugin or mod system", ex.Message);
    }

    [Fact]
    public async Task Install_with_no_dependencies_writes_the_jar_into_plugins_and_tracks_it()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":["1.21.4"],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var handler = new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        Assert.False(result.NeedsDependencyConfirmation);
        var installed = Assert.Single(result.Installed);
        Assert.Equal("proj1", installed.ProjectId);
        Assert.Equal("My Plugin", installed.ProjectTitle);
        Assert.Equal("plugin.jar", installed.FileName);

        var jarPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar");
        Assert.True(File.Exists(jarPath));
        Assert.Equal("hello", await File.ReadAllTextAsync(jarPath));

        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal("proj1", listed.ProjectId);
    }

    [Fact]
    public async Task Install_writes_a_mod_loaders_jar_into_mods_not_plugins()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Fabric);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":["1.21.4"],"loaders":["fabric"],"dependencies":[],
             "files":[{"url":"https://cdn.example/mymod.jar","filename":"mymod.jar","primary":true,"size":5}]}
            """;
        var handler = new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Mod"}""");
        var service = NewService(handler, started.Settings);

        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(started.WorkingDirectory, "mods", "mymod.jar")));
        Assert.False(Directory.Exists(Path.Combine(started.WorkingDirectory, "plugins")));
    }

    [Fact]
    public async Task Install_picks_the_file_marked_primary_when_a_version_has_several()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[
               {"url":"https://cdn.example/sources.jar","filename":"sources.jar","primary":false,"size":5},
               {"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}
             ]}
            """;
        var handler = new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        Assert.Equal("plugin.jar", Assert.Single(result.Installed).FileName);
        Assert.True(File.Exists(Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar")));
        Assert.False(File.Exists(Path.Combine(started.WorkingDirectory, "plugins", "sources.jar")));
    }

    [Fact]
    public async Task Install_needs_confirmation_for_a_required_dependency_and_installs_nothing_yet()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string mainVersionBody = """
            {"id":"main-v1","project_id":"main-proj","name":"n","version_number":"1.0",
             "game_versions":[],"loaders":["paper"],
             "dependencies":[{"version_id":"dep-v1","project_id":null,"file_name":null,"dependency_type":"required"}],
             "files":[{"url":"https://cdn.example/main.jar","filename":"main.jar","primary":true,"size":5}]}
            """;
        const string depVersionBody = """
            {"id":"dep-v1","project_id":"dep-proj","name":"n","version_number":"1.0",
             "game_versions":[],"loaders":["paper"],"files":[{"url":"https://cdn.example/dep.jar","filename":"dep.jar","primary":true,"size":3}]}
            """;
        var handler = new StubHandler()
            .Json(mainVersionBody)
            .Json(depVersionBody)                 // resolves the dependency's project id
            .Json("""{"title":"Dependency Plugin"}""");
        var service = NewService(handler, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, "main-v1", includeDependencies: false, CancellationToken.None);

        Assert.True(result.NeedsDependencyConfirmation);
        var dep = Assert.Single(result.RequiredDependencies);
        Assert.Equal("dep-proj", dep.ProjectId);
        Assert.Equal("Dependency Plugin", dep.ProjectTitle);
        Assert.Empty(result.Installed);

        Assert.False(Directory.Exists(Path.Combine(started.WorkingDirectory, "plugins")));
        Assert.Empty(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Install_with_dependencies_confirmed_installs_both_the_version_and_its_dependency()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string mainVersionBody = """
            {"id":"main-v1","project_id":"main-proj","name":"n","version_number":"1.0",
             "game_versions":[],"loaders":["paper"],
             "dependencies":[{"version_id":"dep-v1","project_id":null,"file_name":null,"dependency_type":"required"}],
             "files":[{"url":"https://cdn.example/main.jar","filename":"main.jar","primary":true,"size":5}]}
            """;
        const string depVersionBody = """
            {"id":"dep-v1","project_id":"dep-proj","name":"n","version_number":"1.0",
             "game_versions":[],"loaders":["paper"],"files":[{"url":"https://cdn.example/dep.jar","filename":"dep.jar","primary":true,"size":3}]}
            """;
        // includeDependencies:true skips the title-gathering step entirely, so this
        // queue has no extra GetProjectTitle/GetVersion call for the dependency
        // beyond resolving its own version and file.
        var handler = new StubHandler()
            .Json(mainVersionBody)
            .Bytes("main-bytes")
            .Json("""{"title":"Main Plugin"}""")
            .Json(depVersionBody)
            .Bytes("dep-bytes")
            .Json("""{"title":"Dependency Plugin"}""");
        var service = NewService(handler, started.Settings);

        var result = await service.InstallAsync(started.Supervisor, "main-v1", includeDependencies: true, CancellationToken.None);

        Assert.False(result.NeedsDependencyConfirmation);
        Assert.Equal(2, result.Installed.Count);
        Assert.Contains(result.Installed, i => i.ProjectId == "main-proj");
        Assert.Contains(result.Installed, i => i.ProjectId == "dep-proj");

        Assert.Equal("main-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "main.jar")));
        Assert.Equal("dep-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "dep.jar")));
        Assert.Equal(2, (await service.ListInstalledAsync(started.Supervisor)).Count);
    }

    [Fact]
    public async Task Reinstalling_the_same_project_replaces_its_tracking_entry_rather_than_duplicating_it()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        string VersionBody(string id, string number) => $$"""
            {"id":"{{id}}","project_id":"proj1","name":"n","version_number":"{{number}}",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var handler = new StubHandler()
            .Json(VersionBody("v1", "1.0.0")).Bytes("v1-bytes").Json("""{"title":"My Plugin"}""")
            .Json(VersionBody("v2", "2.0.0")).Bytes("v2-bytes").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);

        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);
        await service.InstallAsync(started.Supervisor, "v2", includeDependencies: false, CancellationToken.None);

        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal("2.0.0", listed.VersionNumber);
        Assert.Equal("v2-bytes", await File.ReadAllTextAsync(Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar")));
    }

    [Fact]
    public async Task Updating_to_a_differently_named_jar_deletes_the_one_it_replaces()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        string VersionBody(string id, string number, string fileName) => $$"""
            {"id":"{{id}}","project_id":"proj1","name":"n","version_number":"{{number}}",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/{{fileName}}","filename":"{{fileName}}","primary":true,"size":5}]}
            """;
        var handler = new StubHandler()
            .Json(VersionBody("v1", "1.0.0", "plugin-1.0.0.jar")).Bytes("v1-bytes").Json("""{"title":"My Plugin"}""")
            .Json(VersionBody("v2", "2.0.0", "plugin-2.0.0.jar")).Bytes("v2-bytes").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);

        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);
        var result = await service.InstallAsync(started.Supervisor, "v2", includeDependencies: false, CancellationToken.None);

        Assert.Empty(result.Warnings);
        var pluginsDir = Path.Combine(started.WorkingDirectory, "plugins");
        Assert.False(File.Exists(Path.Combine(pluginsDir, "plugin-1.0.0.jar")));
        Assert.True(File.Exists(Path.Combine(pluginsDir, "plugin-2.0.0.jar")));
        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal("plugin-2.0.0.jar", listed.FileName);
    }

    [Fact]
    public async Task Install_leaves_the_previous_jar_untouched_when_the_download_fails()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        string VersionBody(string id, string number, string fileName) => $$"""
            {"id":"{{id}}","project_id":"proj1","name":"n","version_number":"{{number}}",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/{{fileName}}","filename":"{{fileName}}","primary":true,"size":5}]}
            """;
        var handler = new StubHandler()
            .Json(VersionBody("v1", "1.0.0", "plugin-1.0.0.jar")).Bytes("v1-bytes").Json("""{"title":"My Plugin"}""")
            .Json(VersionBody("v2", "2.0.0", "plugin-2.0.0.jar")).Status(HttpStatusCode.InternalServerError);
        var service = NewService(handler, started.Settings);
        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.InstallAsync(started.Supervisor, "v2", includeDependencies: false, CancellationToken.None));

        var pluginsDir = Path.Combine(started.WorkingDirectory, "plugins");
        Assert.Equal("v1-bytes", await File.ReadAllTextAsync(Path.Combine(pluginsDir, "plugin-1.0.0.jar")));
        Assert.Single(Directory.GetFiles(pluginsDir));
    }

    [Fact]
    public async Task Install_leaves_no_temporary_file_behind()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var handler = new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);

        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        Assert.Single(Directory.GetFiles(Path.Combine(started.WorkingDirectory, "plugins")));
    }

    [Fact]
    public async Task Update_reports_a_warning_when_the_stale_jar_cannot_be_deleted()
    {
        if (!OperatingSystem.IsWindows()) return; // POSIX unlink succeeds on an open file

        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        string VersionBody(string id, string number, string fileName) => $$"""
            {"id":"{{id}}","project_id":"proj1","name":"n","version_number":"{{number}}",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/{{fileName}}","filename":"{{fileName}}","primary":true,"size":5}]}
            """;
        var handler = new StubHandler()
            .Json(VersionBody("v1", "1.0.0", "plugin-1.0.0.jar")).Bytes("v1-bytes").Json("""{"title":"My Plugin"}""")
            .Json(VersionBody("v2", "2.0.0", "plugin-2.0.0.jar")).Bytes("v2-bytes").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);
        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        var oldPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin-1.0.0.jar");
        await using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.InstallAsync(started.Supervisor, "v2", includeDependencies: false, CancellationToken.None);

            var warning = Assert.Single(result.Warnings);
            Assert.Contains("plugin-2.0.0.jar", warning);
            Assert.Contains("plugin-1.0.0.jar", warning);
        }

        // The install still went through — the new file is in place and tracked.
        Assert.True(File.Exists(Path.Combine(started.WorkingDirectory, "plugins", "plugin-2.0.0.jar")));
        var listed = Assert.Single(await service.ListInstalledAsync(started.Supervisor));
        Assert.Equal("plugin-2.0.0.jar", listed.FileName);
    }

    [Fact]
    public async Task Remove_deletes_the_file_and_forgets_the_install()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var handler = new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}""");
        var service = NewService(handler, started.Settings);
        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);
        var jarPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar");
        Assert.True(File.Exists(jarPath));

        var removed = await service.RemoveAsync(started.Supervisor, "proj1");

        Assert.True(removed);
        Assert.False(File.Exists(jarPath));
        Assert.Empty(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Remove_reports_a_clear_error_when_the_server_has_never_been_started()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var service = NewService(new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}"""), started.Settings);
        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        // A tracked install can outlive the process that made it — this supervisor
        // shares the tracked install's ServerId but was never started, so its
        // ActiveProfile is null, the same shape ServerScope hands back for a
        // profile nobody has started this run.
        var neverStarted = new ServerSupervisor(
            started.Supervisor.ServerId, new EventBroker(), started.Settings, new HttpClient(),
            NullLogger<ServerSupervisor>.Instance,
            new RconSecretStore(
                started.Settings,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(started.WorkingDirectory, "dpkeys"))),
                NullLogger<RconSecretStore>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(neverStarted, "proj1"));

        Assert.Equal("No server has been started yet.", ex.Message);
        Assert.Single(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Remove_reports_a_clear_error_when_the_file_is_locked()
    {
        if (!OperatingSystem.IsWindows()) return; // POSIX unlink succeeds on an open file

        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var service = NewService(new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}"""), started.Settings);
        await service.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);
        var jarPath = Path.Combine(started.WorkingDirectory, "plugins", "plugin.jar");

        await using (new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(started.Supervisor, "proj1"));
            Assert.Contains("plugin.jar", ex.Message);
        }

        // The tracking row must survive — otherwise the jar is orphaned with
        // nothing left pointing at it and no way to retry from the UI.
        Assert.Single(await service.ListInstalledAsync(started.Supervisor));
    }

    [Fact]
    public async Task Installed_tracking_round_trips_through_disk_for_a_new_service_instance()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper);
        const string versionBody = """
            {"id":"v1","project_id":"proj1","name":"n","version_number":"1.0.0",
             "game_versions":[],"loaders":["paper"],"dependencies":[],
             "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
            """;
        var writer = NewService(new StubHandler().Json(versionBody).Bytes("hello").Json("""{"title":"My Plugin"}"""), started.Settings);
        await writer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        // A fresh ModrinthService, same AppDataPath, no HTTP calls made — this
        // only exercises modrinth-installs.json, not the write path above.
        var reader = NewService(new StubHandler(), started.Settings);

        var listed = Assert.Single(await reader.ListInstalledAsync(started.Supervisor));
        Assert.Equal("proj1", listed.ProjectId);
    }

    // ── Check for updates ────────────────────────────────────────────────

    private static string SimpleVersionBody(string id, string number, string projectId = "proj1") => $$"""
        {"id":"{{id}}","project_id":"{{projectId}}","name":"n","version_number":"{{number}}",
         "game_versions":[],"loaders":["paper"],"dependencies":[],
         "files":[{"url":"https://cdn.example/plugin.jar","filename":"plugin.jar","primary":true,"size":5}]}
        """;

    // Tracked installs are keyed by the supervisor's own ServerId, which the
    // StartedSupervisor test harness assigns independently of the profile's
    // own Id (unlike ServerRegistry in production, which always constructs a
    // supervisor with serverId == profile.Id). CheckUpdatesAsync filters by
    // profile.Id, so tests need a profile whose Id actually matches what got
    // tracked — this builds exactly that, without needing the server started.
    private static ServerProfile ProfileFor(StartedSupervisor started, string minecraftVersion = "1.21.4")
        => new() { Id = started.Supervisor.ServerId, Name = "x", Type = ServerType.Paper, MinecraftVersion = minecraftVersion };

    [Fact]
    public async Task CheckUpdates_flags_a_project_whose_newest_compatible_version_differs_from_installed()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var installer = NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0")).Bytes("bytes").Json("""{"title":"My Plugin"}"""),
            started.Settings);
        await installer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        const string versionsBody = """
            [{"id":"v2","project_id":"proj1","name":"n","version_number":"2.0.0","version_type":"release",
              "game_versions":[],"loaders":["paper"],"files":[]}]
            """;
        var checker = NewService(new StubHandler().Json(versionsBody), started.Settings);

        var status = Assert.Single(await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None));

        Assert.Equal("proj1", status.ProjectId);
        Assert.Equal("v1", status.InstalledVersionId);
        Assert.Equal("v2", status.LatestVersionId);
        Assert.Equal("2.0.0", status.LatestVersionNumber);
        Assert.True(status.UpdateAvailable);
        Assert.Null(status.Unavailable);
    }

    [Fact]
    public async Task CheckUpdates_reports_up_to_date_when_the_newest_version_is_the_installed_one()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var installer = NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0")).Bytes("bytes").Json("""{"title":"My Plugin"}"""),
            started.Settings);
        await installer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        var checker = NewService(new StubHandler().Json($"[{SimpleVersionBody("v1", "1.0.0")}]"), started.Settings);

        var status = Assert.Single(await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None));

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.Unavailable);
    }

    [Fact]
    public async Task CheckUpdates_marks_a_project_with_no_compatible_version_left_as_unavailable()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var installer = NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0")).Bytes("bytes").Json("""{"title":"My Plugin"}"""),
            started.Settings);
        await installer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        var checker = NewService(new StubHandler().Json("[]"), started.Settings);

        var status = Assert.Single(await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None));

        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.Unavailable);
    }

    [Fact]
    public async Task CheckUpdates_survives_one_project_failing_and_still_checks_the_rest()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        await NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0", "proj1")).Bytes("bytes1").Json("""{"title":"Plugin One"}"""),
            started.Settings).InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);
        await NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0", "proj2")).Bytes("bytes2").Json("""{"title":"Plugin Two"}"""),
            started.Settings).InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        var checker = NewService(
            new StubHandler().Status(HttpStatusCode.NotFound).Json($"[{SimpleVersionBody("v2", "2.0.0", "proj2")}]"),
            started.Settings);

        var results = await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None);

        Assert.Equal(2, results.Count);
        var proj1 = results.Single(r => r.ProjectId == "proj1");
        Assert.NotNull(proj1.Unavailable);
        var proj2 = results.Single(r => r.ProjectId == "proj2");
        Assert.Null(proj2.Unavailable);
        Assert.True(proj2.UpdateAvailable);
    }

    [Fact]
    public async Task CheckUpdates_returns_nothing_for_a_profile_with_no_tracked_installs_without_making_a_request()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var handler = new StubHandler(); // no responses queued — a call would throw
        var checker = NewService(handler, started.Settings);

        var results = await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task CheckUpdates_filters_by_the_profiles_loader_and_minecraft_version()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var installer = NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0")).Bytes("bytes").Json("""{"title":"My Plugin"}"""),
            started.Settings);
        await installer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        var handler = new StubHandler().Json($"[{SimpleVersionBody("v1", "1.0.0")}]");
        var checker = NewService(handler, started.Settings);

        await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None);

        var uri = handler.RequestUris[0];
        Assert.Equal("""["paper","spigot","bukkit"]""", QueryParam(uri, "loaders"));
        Assert.Equal("""["1.21.4"]""", QueryParam(uri, "game_versions"));
    }

    [Fact]
    public async Task CheckUpdates_works_for_a_profile_this_process_never_started()
    {
        await using var started = await StartedSupervisor.CreateAsync(ServerType.Paper, "1.21.4");
        var installer = NewService(
            new StubHandler().Json(SimpleVersionBody("v1", "1.0.0")).Bytes("bytes").Json("""{"title":"My Plugin"}"""),
            started.Settings);
        await installer.InstallAsync(started.Supervisor, "v1", includeDependencies: false, CancellationToken.None);

        // ProfileFor builds a bare profile sharing only the id — no
        // ServerSupervisor.StartAsync runs against it in this test.
        var checker = NewService(new StubHandler().Json($"[{SimpleVersionBody("v2", "2.0.0")}]"), started.Settings);

        var status = Assert.Single(await checker.CheckUpdatesAsync(ProfileFor(started), CancellationToken.None));

        Assert.True(status.UpdateAvailable);
    }
}
