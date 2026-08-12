using CraftConsole.Infrastructure.Http;
using CraftConsole.Web.Services;
using Xunit;

namespace CraftConsole.Tests.Web;

public class InstalledJarWriterTests : IDisposable
{
    private static readonly DownloadService Downloader = new(new HttpClient());
    private readonly string _tempDir;

    public InstalledJarWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-jar-writer-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("sub/evil.jar")]
    [InlineData("plugins/../../evil.jar")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public async Task WriteAsync_rejects_a_file_name_that_is_not_a_plain_file_name(string fileName)
    {
        // The guard must fire before any network access — no fake/working downloader
        // handler is wired up, so a false negative here would surface as a real
        // outbound request failure instead of silently passing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstalledJarWriter.WriteAsync(
                Downloader, "http://example.invalid/x.jar", _tempDir, fileName, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("../old.jar")]
    [InlineData("sub/old.jar")]
    public async Task WriteAsync_rejects_an_unsafe_replaced_file_name_even_when_the_new_name_is_safe(string replacedFileName)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstalledJarWriter.WriteAsync(
                Downloader, "http://example.invalid/x.jar", _tempDir, "new.jar", replacedFileName, CancellationToken.None));
    }
}
