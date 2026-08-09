using System.IO.Compression;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Plugins, the config-file editor, and the issues feed.</summary>
public static class WorkspaceApi
{
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yml", ".yaml", ".json", ".txt", ".properties", ".log"
    };

    private const long MaxEditableBytes = 2 * 1024 * 1024;

    // Generous rather than tight: a world backup or a modpack bundle is
    // exactly what this exists to move, and both can run into the hundreds
    // of MB. Referenced from Program.cs to raise the framework's own default
    // multipart limit (128 MB) to match — without that, a large upload is
    // rejected by ASP.NET Core before this file's own check ever runs.
    internal const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    public record FileNodeDto(string Name, string Path, bool IsDirectory, bool IsEditable, long Size, List<FileNodeDto> Children);
    public record FileContentRequest(string Path, string Content);
    public record UploadResult(string Path, long Size);

    public static void MapWorkspaceApi(this IEndpointRouteBuilder app)
    {
        // ── Issues ────────────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/issues", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(new { Issues = sup.IssuesSnapshot() }, Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/issues", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(new { Issues = sup?.IssuesSnapshot() ?? [] }, Json.Options);
        }).RequireRole(Role.Operator);

        app.MapDelete("/api/servers/{id:guid}/issues", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            sup.ClearIssues();
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        app.MapDelete("/api/issues", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            (await ServerScope.ResolveActiveAsync(profiles, registry))?.ClearIssues();
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        // ── Plugins ───────────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/plugins", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(PluginsSnapshot(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/plugins", async (ProfilesService profiles, ServerRegistry registry) =>
            Results.Json(PluginsSnapshot(await ServerScope.ResolveActiveAsync(profiles, registry)), Json.Options))
            .RequireRole(Role.Admin);

        app.MapPost("/api/servers/{id:guid}/plugins/{fileName}/disable", async (Guid id, string fileName, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? DisablePlugin(sup, fileName)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/plugins/{fileName}/disable", async (string fileName, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : DisablePlugin(sup, fileName);
        }).RequireRole(Role.Admin);

        // ── File editor ───────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/files/tree", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(FileTreeSnapshot(sup), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/files/tree", async (ProfilesService profiles, ServerRegistry registry) =>
            Results.Json(FileTreeSnapshot(await ServerScope.ResolveActiveAsync(profiles, registry)), Json.Options))
            .RequireRole(Role.Admin);

        app.MapGet("/api/servers/{id:guid}/files/content", async (Guid id, string path, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? ReadFileContent(sup, path)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/files/content", async (string path, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : ReadFileContent(sup, path);
        }).RequireRole(Role.Admin);

        app.MapPut("/api/servers/{id:guid}/files/content", async (Guid id, FileContentRequest req, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await WriteFileContent(sup, req)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPut("/api/files/content", async (FileContentRequest req, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : await WriteFileContent(sup, req);
        }).RequireRole(Role.Admin);

        // ── File transfer ─────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/files/download", async (Guid id, string path, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? DownloadFile(sup, path)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapGet("/api/files/download", async (string path, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : DownloadFile(sup, path);
        }).RequireRole(Role.Admin);

        app.MapPost("/api/servers/{id:guid}/files/upload", async (Guid id, HttpRequest request, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? await UploadFile(sup, request)
                : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/files/upload", async (HttpRequest request, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return sup is null
                ? Results.BadRequest(new { Message = ServerScope.NoServerStarted })
                : await UploadFile(sup, request);
        }).RequireRole(Role.Admin);
    }

    private static object PluginsSnapshot(ServerSupervisor? sup)
    {
        if (sup is null)
            return new { Available = false, Reason = ServerScope.NoServerStarted, Folder = (string?)null, Plugins = new List<PluginInfo>() };
        if (sup.LocalFileUnavailableReason is { } reason)
            return new { Available = false, Reason = reason, Folder = (string?)null, Plugins = new List<PluginInfo>() };

        var folder = PluginsFolder(sup);
        if (folder is null || !Directory.Exists(folder))
            return new { Available = true, Reason = (string?)null, Folder = folder, Plugins = new List<PluginInfo>() };

        var plugins = Directory.GetFiles(folder, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(TryReadPluginYaml)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new { Available = true, Reason = (string?)null, Folder = folder, Plugins = plugins };
    }

    private static IResult DisablePlugin(ServerSupervisor sup, string fileName)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            return Results.BadRequest(new { Message = reason });

        var folder = PluginsFolder(sup)!; // LocalFileUnavailableReason already ruled out null here

        // fileName comes from the client — never allow it to escape the plugins folder
        if (!IsSafeFileName(fileName))
            return Results.BadRequest(new { Message = "Invalid file name." });

        var source = Path.Combine(folder, fileName);
        if (!File.Exists(source)) return Results.NotFound();

        var disabledDir = Path.Combine(folder, "disabled");
        Directory.CreateDirectory(disabledDir);
        File.Move(source, Path.Combine(disabledDir, fileName), overwrite: true);
        return Results.NoContent();
    }

    private static object FileTreeSnapshot(ServerSupervisor? sup)
    {
        if (sup is null)
            return new { Available = false, Reason = ServerScope.NoServerStarted, Root = (string?)null, Nodes = new List<FileNodeDto>() };
        if (sup.LocalFileUnavailableReason is { } reason)
            return new { Available = false, Reason = reason, Root = (string?)null, Nodes = new List<FileNodeDto>() };

        var root = sup.ActiveProfile!.WorkingDirectory;
        if (!Directory.Exists(root))
            return new { Available = true, Reason = (string?)null, Root = root, Nodes = new List<FileNodeDto>() };

        var node = BuildNode(root, root);
        return new { Available = true, Reason = (string?)null, Root = root, Nodes = node?.Children ?? [] };
    }

    private static IResult ReadFileContent(ServerSupervisor sup, string path)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            return Results.BadRequest(new { Message = reason });
        if (ResolveJailedPath(sup, path) is not { } fullPath)
            return Results.BadRequest(new { Message = "Path is outside the server directory." });
        if (!EditableExtensions.Contains(Path.GetExtension(fullPath)))
            return Results.BadRequest(new { Message = "This file type cannot be edited." });
        if (!File.Exists(fullPath)) return Results.NotFound();
        if (new FileInfo(fullPath).Length > MaxEditableBytes)
            return Results.BadRequest(new { Message = "File is too large to edit (2 MB limit)." });

        return Results.Json(new { Path = path, Content = File.ReadAllText(fullPath) }, Json.Options);
    }

    private static async Task<IResult> WriteFileContent(ServerSupervisor sup, FileContentRequest req)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            return Results.BadRequest(new { Message = reason });
        if (ResolveJailedPath(sup, req.Path) is not { } fullPath)
            return Results.BadRequest(new { Message = "Path is outside the server directory." });
        if (!EditableExtensions.Contains(Path.GetExtension(fullPath)))
            return Results.BadRequest(new { Message = "This file type cannot be edited." });
        if (!File.Exists(fullPath)) return Results.NotFound();

        await File.WriteAllTextAsync(fullPath, req.Content);
        return Results.NoContent();
    }

    /// <summary>
    /// Unlike ReadFileContent, download has no extension or size gate — a
    /// world backup or the server jar is exactly what this exists for.
    /// </summary>
    private static IResult DownloadFile(ServerSupervisor sup, string path)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            return Results.BadRequest(new { Message = reason });
        if (ResolveJailedPath(sup, path) is not { } fullPath)
            return Results.BadRequest(new { Message = "Path is outside the server directory." });
        if (!File.Exists(fullPath))
            return Results.NotFound();

        return Results.File(fullPath, "application/octet-stream", Path.GetFileName(fullPath));
    }

    private static async Task<IResult> UploadFile(ServerSupervisor sup, HttpRequest request)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            return Results.BadRequest(new { Message = reason });
        if (!request.HasFormContentType)
            return Results.BadRequest(new { Message = "Expected a multipart upload." });

        // Kestrel's own per-request body size limit (~28.6 MB by default) sits
        // in front of everything else here — MaxUploadBytes below is the real,
        // enforced cap; this only lifts the transport-level ceiling so a
        // legitimate large upload can reach that check at all.
        var sizeFeature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxUploadBytes;

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { Message = "No file was included in the upload." });
        if (file.Length > MaxUploadBytes)
            return Results.BadRequest(new { Message = $"File is too large (limit {MaxUploadBytes / (1024 * 1024 * 1024)} GB)." });

        // "" (root) is a legitimate destination, handled directly rather than
        // through ResolveJailedPath: passing it "." there normalizes away the
        // trailing separator Path.GetFullPath adds to the root for comparison,
        // so the containment check never matches its own root.
        var destinationDir = form["path"].ToString();
        string destFull;
        if (string.IsNullOrEmpty(destinationDir))
        {
            destFull = sup.ActiveProfile!.WorkingDirectory;
        }
        else if (ResolveJailedPath(sup, destinationDir) is { } resolved)
        {
            destFull = resolved;
        }
        else
        {
            return Results.BadRequest(new { Message = "Destination is outside the server directory." });
        }
        if (!Directory.Exists(destFull))
            return Results.BadRequest(new { Message = "Destination folder does not exist." });

        // The browser supplies this name — not implicitly safe just because
        // it arrived attached to a file, same reasoning as plugin disable.
        var fileName = Path.GetFileName(file.FileName);
        if (!IsSafeFileName(fileName))
            return Results.BadRequest(new { Message = "Invalid file name." });

        var extract = string.Equals(form["extract"], "true", StringComparison.OrdinalIgnoreCase);
        if (extract)
        {
            if (!string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { Message = "Only a .zip archive can be extracted on upload." });

            // Saved to a temp file first — BackupService.ExtractArchive reads
            // from a path (it opens the zip with random access to check every
            // entry before writing any of them), so the upload has to land on
            // disk before extraction can start either way.
            var tempPath = Path.GetTempFileName();
            try
            {
                await using (var tempStream = File.Create(tempPath))
                    await file.CopyToAsync(tempStream);

                BackupService.ExtractArchive(tempPath, destFull);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }

        var overwrite = string.Equals(form["overwrite"], "true", StringComparison.OrdinalIgnoreCase);
        var targetPath = Path.Combine(destFull, fileName);
        if (File.Exists(targetPath) && !overwrite)
            return Results.Conflict(new { Message = $"\"{fileName}\" already exists." });

        await using (var destStream = File.Create(targetPath))
            await file.CopyToAsync(destStream);

        var relative = Path.GetRelativePath(sup.ActiveProfile!.WorkingDirectory, targetPath).Replace('\\', '/');
        return Results.Json(new UploadResult(relative, file.Length), Json.Options);
    }

    private static string? PluginsFolder(ServerSupervisor sup)
        => sup.ActiveProfile is { } profile
            ? Path.Combine(profile.WorkingDirectory, "plugins")
            : null;

    /// <summary>Resolves a client-supplied relative path, refusing anything outside the server dir.</summary>
    private static string? ResolveJailedPath(ServerSupervisor sup, string relativePath)
        => sup.ActiveProfile?.WorkingDirectory is { } root
            ? ResolveJailedPath(root, relativePath)
            : null;

    /// <summary>
    /// Containment check for a client-supplied path. Returns the absolute path when
    /// it stays inside <paramref name="root"/>, otherwise null.
    ///
    /// Kept separate from the supervisor so it can be tested directly — this is the
    /// only thing standing between the file editor and the rest of the filesystem.
    /// </summary>
    internal static string? ResolveJailedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        string rootFull, candidate;
        try
        {
            rootFull = Path.GetFullPath(root);
            // An absolute or rooted relativePath wins over root in Path.Combine, so
            // the comparison below is what actually rejects it — not Combine itself.
            candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // malformed path — treat as out of bounds
        }

        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    /// <summary>Rejects a plugin file name that tries to address anything but a file in the folder.</summary>
    internal static bool IsSafeFileName(string fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && !fileName.Contains("..")
           && !fileName.Contains('/')
           && !fileName.Contains('\\')
           && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // Every file appears in the tree now, not just editable ones — a world
    // save or the server jar needs to be visible to be downloaded. The
    // recursive walk already touches every file to build this tree; keeping
    // non-editable ones out only shrank the JSON, it never saved a directory
    // read. IsEditable gates the Edit action client-side; ReadFileContent and
    // WriteFileContent keep their own EditableExtensions check server-side
    // regardless of what the tree reports.
    private static FileNodeDto? BuildNode(string path, string root)
    {
        if (File.Exists(path))
        {
            return new FileNodeDto(
                Path.GetFileName(path),
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                IsDirectory: false,
                IsEditable: EditableExtensions.Contains(Path.GetExtension(path)),
                new FileInfo(path).Length,
                []);
        }

        if (!Directory.Exists(path)) return null;

        var children = new List<FileNodeDto>();
        try
        {
            foreach (var file in Directory.GetFiles(path))
                if (BuildNode(file, root) is { } child) children.Add(child);
            foreach (var dir in Directory.GetDirectories(path))
                if (BuildNode(dir, root) is { } child) children.Add(child);
        }
        catch { /* access denied — skip subtree */ }

        if (children.Count == 0) return null;

        return new FileNodeDto(
            Path.GetFileName(path),
            path == root ? "" : Path.GetRelativePath(root, path).Replace('\\', '/'),
            IsDirectory: true,
            IsEditable: false,
            0,
            [.. children
                .OrderByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static PluginInfo TryReadPluginYaml(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        var name = Path.GetFileNameWithoutExtension(jarPath);
        string description = "", author = "", version = "";

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("plugin.yml") ?? zip.GetEntry("plugin.yaml");
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open());
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith("name:")) name = line[5..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("version:")) version = line[8..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("description:")) description = line[12..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("author:")) author = line[7..].Trim().Trim('\'', '"');
                }
            }
        }
        catch { /* bad zip or no plugin.yml */ }

        return new PluginInfo
        {
            FileName = fileName,
            Name = name,
            Version = version,
            Description = description,
            Author = author,
        };
    }
}
