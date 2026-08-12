using CraftConsole.Infrastructure.Config;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CraftConsole.Tests.Servers;

/// <summary>
/// Direct unit tests for ServerSupervisor's own defences. None of these need a
/// real or fake child process: SendCommandAsync's line-break guard runs before
/// the "is a server running" check, and SimulateOutput drives the console
/// parsing pipeline without a process at all — see ServerProcessManagerTests
/// for the end-to-end version against a real (fake) process.
/// </summary>
public class ServerSupervisorTests
{
    private sealed class TestSupervisor : IAsyncDisposable
    {
        public ServerSupervisor Supervisor { get; }
        private readonly string _dir;

        public TestSupervisor()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cc-supervisor-test-" + Guid.NewGuid());
            Directory.CreateDirectory(_dir);

            var settings = new SettingsHolder(_dir);
            var secrets = new RconSecretStore(
                settings,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "dpkeys"))),
                NullLogger<RconSecretStore>.Instance);

            Supervisor = new ServerSupervisor(
                Guid.NewGuid(), new EventBroker(), settings, new HttpClient(),
                NullLogger<ServerSupervisor>.Instance, secrets);
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string? PropertyOf(object dto, string name)
        => (string?)dto.GetType().GetProperty(name)!.GetValue(dto);

    // ── Command injection guard ──────────────────────────────────────────

    [Theory]
    [InlineData("kick Steve\nop Steve")]
    [InlineData("kick Steve\rop Steve")]
    public async Task SendCommandAsync_rejects_a_command_containing_an_embedded_line_break(string command)
    {
        await using var test = new TestSupervisor();

        await test.Supervisor.SendCommandAsync(command);

        var console = test.Supervisor.ConsoleSnapshot();
        Assert.Contains(console, e => e.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(console, e => e.Message.StartsWith("> ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendCommandAsync_still_accepts_an_ordinary_single_line_command()
    {
        await using var test = new TestSupervisor();

        await test.Supervisor.SendCommandAsync("kick Steve Being AFK");

        Assert.Contains(test.Supervisor.ConsoleSnapshot(), e => e.Message == "> kick Steve Being AFK");
    }

    // ── Unbounded player-list growth ─────────────────────────────────────

    [Fact]
    public async Task Players_list_evicts_the_oldest_entry_once_it_exceeds_the_cap()
    {
        await using var test = new TestSupervisor();

        for (var i = 0; i < 501; i++)
            test.Supervisor.SimulateOutput($"Player{i} joined the game");

        var usernames = test.Supervisor.PlayersSnapshot()
            .Select(p => PropertyOf(p, "Username"))
            .ToList();

        Assert.Equal(500, usernames.Count);
        Assert.DoesNotContain("Player0", usernames);
        Assert.Contains("Player500", usernames);
    }

    // ── Unvalidated ip capture ───────────────────────────────────────────

    [Fact]
    public async Task A_login_line_with_an_unparsable_ip_is_recorded_with_no_ip()
    {
        await using var test = new TestSupervisor();

        test.Supervisor.SimulateOutput("Steve[/not-an-ip:51234] logged in with entity id 261");

        var player = Assert.Single(test.Supervisor.PlayersSnapshot());
        Assert.Null(PropertyOf(player, "IpAddress"));
    }

    [Fact]
    public async Task A_login_line_with_a_real_ip_is_recorded_normally()
    {
        await using var test = new TestSupervisor();

        test.Supervisor.SimulateOutput("Steve[/192.168.1.50:51234] logged in with entity id 261");

        var player = Assert.Single(test.Supervisor.PlayersSnapshot());
        Assert.Equal("192.168.1.50", PropertyOf(player, "IpAddress"));
    }
}
