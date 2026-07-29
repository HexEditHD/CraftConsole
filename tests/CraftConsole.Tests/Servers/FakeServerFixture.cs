using System.Collections.Concurrent;
using CraftConsole.Core.Models;
using CraftConsole.Core.Servers;

namespace CraftConsole.Tests.Servers;

/// <summary>
/// Locates the compiled fake-server executable and builds profiles that point
/// <see cref="ServerProfile.JavaPath"/> at it. The manager then launches it the
/// same way it launches java; the java-style arguments are ignored by the fake.
/// </summary>
public static class FakeServer
{
    private static readonly Lazy<string> ExecutablePath = new(Locate);

    public static string Path => ExecutablePath.Value;

    public static ServerProfile Profile(string workingDirectory) => new()
    {
        Name = "Fake",
        JavaPath = Path,
        JarPath = System.IO.Path.Combine(workingDirectory, "fake-server.jar"),
        WorkingDirectory = workingDirectory,
        MinRamMb = 512,
        MaxRamMb = 1024,
    };

    private static string Locate()
    {
        // The test assembly and the fake server are built into sibling output
        // folders under the same configuration/TFM.
        var testDir = AppContext.BaseDirectory;                                   // .../tests/CraftConsole.Tests/bin/<cfg>/<tfm>/
        var tfm = new DirectoryInfo(testDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var configuration = tfm.Parent!.Name;

        var repoTestsDir = tfm.Parent!.Parent!.Parent!.Parent!.FullName;          // .../tests/
        var exeName = OperatingSystem.IsWindows()
            ? "CraftConsole.FakeServer.exe"
            : "CraftConsole.FakeServer";

        var candidate = System.IO.Path.Combine(
            repoTestsDir, "CraftConsole.FakeServer", "bin", configuration, tfm.Name, exeName);

        if (File.Exists(candidate)) return candidate;

        throw new FileNotFoundException(
            $"Fake server not found at '{candidate}'. It is referenced by the test project so a " +
            "normal build produces it; run `dotnet build` on the solution.", candidate);
    }
}

/// <summary>
/// Runs a <see cref="ServerProcessManager"/> against the fake server and records
/// everything it emits, with helpers to await a condition instead of sleeping.
/// </summary>
public sealed class FakeServerRun : IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly IDisposable _consoleSub;
    private readonly IDisposable _statusSub;

    public ServerProcessManager Manager { get; }
    public ConcurrentQueue<ConsoleEntry> Console { get; } = new();
    public ConcurrentQueue<ServerStatus> Statuses { get; } = new();

    public FakeServerRun(string mode = "normal", TimeSpan? stopTimeout = null, int? bootMs = null)
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "cc-fake-" + Guid.NewGuid());
        Directory.CreateDirectory(_workingDirectory);

        // The fake server reads its behaviour from the environment, which the child
        // process inherits from the test host.
        Environment.SetEnvironmentVariable("CRAFTCONSOLE_FAKE_MODE", mode);
        Environment.SetEnvironmentVariable(
            "CRAFTCONSOLE_FAKE_BOOT_MS", bootMs?.ToString() ?? "60");

        Manager = new ServerProcessManager(FakeServer.Profile(_workingDirectory), stopTimeout);
        _consoleSub = Manager.ConsoleOutput.Subscribe(Console.Enqueue);
        _statusSub = Manager.StatusChanged.Subscribe(Statuses.Enqueue);
    }

    public string WorkingDirectory => _workingDirectory;

    public IReadOnlyList<string> Messages => [.. Console.Select(e => e.Message)];

    /// <summary>Polls until <paramref name="condition"/> holds, or fails the test on timeout.</summary>
    public async Task WaitUntilAsync(
        Func<bool> condition, string because, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Timed out after {timeoutMs}ms waiting for: {because}.{Environment.NewLine}" +
            $"Status: {Manager.Status}. Console:{Environment.NewLine}" +
            string.Join(Environment.NewLine, Messages));
    }

    public Task WaitForStatusAsync(ServerStatus status, int timeoutMs = 15_000)
        => WaitUntilAsync(() => Manager.Status == status, $"status to become {status}", timeoutMs);

    public Task WaitForConsoleAsync(string fragment, int timeoutMs = 15_000)
        => WaitUntilAsync(
            () => Messages.Any(m => m.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            $"console to contain \"{fragment}\"",
            timeoutMs);

    public async ValueTask DisposeAsync()
    {
        _consoleSub.Dispose();
        _statusSub.Dispose();
        await Manager.DisposeAsync();

        try { Directory.Delete(_workingDirectory, recursive: true); }
        catch { /* the child may still hold a handle briefly */ }
    }
}
