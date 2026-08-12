using CraftConsole.Infrastructure.Http;

namespace CraftConsole.Web.Services;

/// <summary>
/// Shared by ModrinthService and CurseForgeService for the one write both
/// installers do: put a downloaded jar in place, and if it's replacing a
/// previously-installed file under a different name, remove the old one so
/// the server doesn't end up loading two copies of the same plugin.
/// </summary>
internal static class InstalledJarWriter
{
    /// <summary>
    /// Downloads to a temp file and moves it into place — a failed download
    /// never touches the file it would have replaced. Returns null on a clean
    /// write, or a message for the operator when the new file is in place but
    /// the one it replaced could not be removed (most often: the server is
    /// running and still holds it open). That's a partial success, not a
    /// failure — the correct file is on disk either way.
    /// </summary>
    public static async Task<string?> WriteAsync(
        DownloadService downloader, string url, string directory,
        string fileName, string? replacedFileName, CancellationToken ct)
    {
        // Both current callers already sanitize via Path.GetFileName before calling
        // in, but this method has no way to know that of a future caller — refuse
        // to trust either name as anything but a bare filename.
        if (!IsPlainFileName(fileName))
            throw new InvalidOperationException($"\"{fileName}\" is not a valid plain file name.");
        if (replacedFileName is not null && !IsPlainFileName(replacedFileName))
            throw new InvalidOperationException($"\"{replacedFileName}\" is not a valid plain file name.");

        var destination = Path.Combine(directory, fileName);

        // Dot-prefixed and never .jar: WorkspaceApi's plugin scan and the
        // Minecraft server itself both glob plugins/mods for *.jar, and a
        // half-downloaded file must never be a candidate for either. Same
        // temp-then-move shape as JsonFileStore.SaveAsync, for the same reason.
        var temp = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.part");
        try
        {
            await downloader.DownloadFileAsync(url, temp, null, ct);

            try
            {
                File.Move(temp, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not write \"{fileName}\" — if the server is running it may be " +
                    "holding the old file open. Stop it and try again.", ex);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { /* already moved, or never created */ }
        }

        // Only ever reached once the new file is safely in place.
        if (replacedFileName is null
            || string.Equals(replacedFileName, fileName, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var oldPath = Path.Combine(directory, replacedFileName);
            if (File.Exists(oldPath)) File.Delete(oldPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The install still succeeded — leaking a stale jar is the lesser
            // failure next to refusing an update the operator asked for.
            return $"\"{fileName}\" was installed, but the previous file \"{replacedFileName}\" " +
                   "could not be removed — if the server is running it may be holding it open. " +
                   "Delete it manually once the server is stopped.";
        }
    }

    private static bool IsPlainFileName(string fileName)
        => !string.IsNullOrEmpty(fileName)
           && fileName is not ("." or "..")
           && fileName == Path.GetFileName(fileName);
}
