using System.Text.Json.Nodes;
using QBModsBrowser.Server.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Extracts downloaded archives into the local mods folder and reports installed mod ids.
public class ModInstallationService
{
    private readonly ILogger _log;

    // Creates installer with scoped logger used during extraction operations.
    public ModInstallationService(ILogger logger)
    {
        _log = logger.ForContext<ModInstallationService>();
    }

    // Installs one archive by extracting everything to a temp directory first, then copying
    // detected mod roots into the mods folder. The full-archive-first approach means even
    // solid 7z archives are handled in a single sequential decompression pass, avoiding the
    // catastrophic re-decompress-from-start penalty that entry-by-entry access causes.
    public async Task<ModArchiveInstallResult> InstallFromArchiveAsync(
        string archivePath,
        string modsFolder,
        Action<long, long>? onExtractProgress = null)
    {
        var result = new ModArchiveInstallResult();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qbmods-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                archive.WriteToDirectory(tempRoot, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }

            var modInfoPaths = GetValidModInfoPaths(tempRoot);
            if (modInfoPaths.Count == 0)
                throw new InvalidOperationException("No mod_info.json found in archive");

            var totalFiles = 0L;
            foreach (var modInfoPath in modInfoPaths)
            {
                var rel = Path.GetRelativePath(tempRoot, modInfoPath).Replace('\\', '/');
                var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var sourceRoot = parts.Length == 1
                    ? tempRoot
                    : Path.Combine(tempRoot, string.Join(Path.DirectorySeparatorChar, parts[..^1]));
                if (Directory.Exists(sourceRoot))
                    totalFiles += Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).LongCount();
            }

            long processedFiles = 0;
            onExtractProgress?.Invoke(processedFiles, totalFiles);

            foreach (var modInfoPath in modInfoPaths)
            {
                var rel = Path.GetRelativePath(tempRoot, modInfoPath).Replace('\\', '/');
                var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var isLooseFiles = parts.Length == 1;
                var sourceRoot = isLooseFiles
                    ? tempRoot
                    : Path.Combine(tempRoot, string.Join(Path.DirectorySeparatorChar, parts[..^1]));
                if (!Directory.Exists(sourceRoot)) continue;

                var (detectedModId, archiveVersion) = await ReadModInfoFromPathAsync(modInfoPath);
                var targetFolderName = isLooseFiles
                    ? (detectedModId ?? Path.GetFileNameWithoutExtension(archivePath))
                    : parts[^2];
                var targetPath = Path.Combine(modsFolder, targetFolderName);

                if (Directory.Exists(targetPath))
                {
                    var installedVersion = ReadInstalledVersionFromFolder(targetPath);
                    if (!string.IsNullOrWhiteSpace(archiveVersion)
                        && !string.IsNullOrWhiteSpace(installedVersion)
                        && string.Equals(archiveVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(detectedModId))
                            result.ModIds.Add(detectedModId);
                        _log.Information(
                            "Skipped install of {Folder}: already at version {Version}",
                            targetFolderName, installedVersion);
                        continue;
                    }

                    try
                    {
                        Directory.Delete(targetPath, true);
                        _log.Information("Deleted old mod folder for update: {Folder}", Path.GetFileName(targetPath));
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Could not delete existing mod folder before update");
                    }
                }

                Directory.CreateDirectory(targetPath);
                var extracted = await CopyDirectoryContentsAsync(sourceRoot, targetPath, () =>
                {
                    processedFiles++;
                    onExtractProgress?.Invoke(processedFiles, totalFiles);
                });

                if (!string.IsNullOrWhiteSpace(detectedModId))
                    result.ModIds.Add(detectedModId);

                result.InstalledFolderPaths.Add(Path.GetFullPath(targetPath));
                _log.Information("Installed mod {ModFolder} (id={ModId}): {Count} files extracted",
                    targetFolderName, detectedModId ?? "unknown", extracted);
            }

            var uniqueModIds = result.ModIds
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.ModIds.Clear();
            result.ModIds.AddRange(uniqueModIds);
            return result;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // Reads archive metadata and returns detected mod ids without extracting files.
    public async Task<List<string>> DetectArchiveModIdsAsync(string archivePath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var modInfoEntries = GetValidModInfoEntries(entries);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modInfoEntry in modInfoEntries)
        {
            var (id, _) = await ReadModInfoFromEntry(modInfoEntry);
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }

        return ids.ToList();
    }

    // Finds valid mod_info.json files in a directory, ignoring hidden/special directories.
    private static List<string> GetValidModInfoPaths(string root)
    {
        return Directory.EnumerateFiles(root, "mod_info.json", SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(root, p).Replace('\\', '/');
                var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return !parts.Any(x => x.StartsWith('.') || x.StartsWith('_'));
            })
            .ToList();
    }

    // Copies all files from one directory to another and ticks progress per copied file.
    private static async Task<int> CopyDirectoryContentsAsync(string sourceRoot, string targetRoot, Action onFileCopied)
    {
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
        foreach (var src in files)
        {
            var rel = Path.GetRelativePath(sourceRoot, src);
            var dest = Path.Combine(targetRoot, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(destDir))
                Directory.CreateDirectory(destDir);
            await using var inStream = File.OpenRead(src);
            await using var outStream = File.Create(dest);
            await inStream.CopyToAsync(outStream);
            onFileCopied();
        }

        return files.Count;
    }

    // Reads mod id/version from a mod_info.json path extracted to disk.
    private static async Task<(string? Id, string? Version)> ReadModInfoFromPathAsync(string modInfoPath)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(modInfoPath);
            var json = JsonFixHelper.FixJson(raw);
            var node = JsonNode.Parse(json);
            var id = node?["id"]?.GetValue<string>();
            var version = ExtractVersionString(node?["version"]);
            return (string.IsNullOrWhiteSpace(id) ? null : id, version);
        }
        catch
        {
            return (null, null);
        }
    }

    // Reads mod id/version from an archive entry without extracting to disk.
    private static async Task<(string? Id, string? Version)> ReadModInfoFromEntry(IArchiveEntry entry)
    {
        try
        {
            await using var stream = entry.OpenEntryStream();
            using var reader = new StreamReader(stream);
            var raw = await reader.ReadToEndAsync();
            var json = JsonFixHelper.FixJson(raw);
            var node = JsonNode.Parse(json);
            var id = node?["id"]?.GetValue<string>();
            var version = ExtractVersionString(node?["version"]);
            return (string.IsNullOrWhiteSpace(id) ? null : id, version);
        }
        catch
        {
            return (null, null);
        }
    }

    // Reads the version string from an already-installed mod folder's mod_info.json.
    private static string? ReadInstalledVersionFromFolder(string folderPath)
    {
        var enabledPath = Path.Combine(folderPath, "mod_info.json");
        if (!File.Exists(enabledPath)) return null;
        try
        {
            var raw = File.ReadAllText(enabledPath);
            var json = JsonFixHelper.FixJson(raw);
            var node = JsonNode.Parse(json);
            return ExtractVersionString(node?["version"]);
        }
        catch { return null; }
    }

    // Normalizes version nodes that may be a plain string or a { major, minor, patch } object.
    private static string? ExtractVersionString(JsonNode? versionNode)
    {
        if (versionNode == null) return null;
        if (versionNode is JsonValue val)
            return val.ToString();
        if (versionNode is JsonObject obj)
        {
            var major = obj["major"]?.ToString() ?? "0";
            var minor = obj["minor"]?.ToString();
            var patch = obj["patch"]?.ToString();
            var parts = new List<string> { major };
            if (minor != null) parts.Add(minor);
            if (patch != null) parts.Add(patch);
            return string.Join(".", parts);
        }
        return versionNode.ToString();
    }

    // Filters archive entries down to valid mod_info.json files.
    private static List<IArchiveEntry> GetValidModInfoEntries(List<IArchiveEntry> entries)
    {
        return entries
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .Where(e => Path.GetFileName(e.Key!).Equals("mod_info.json", StringComparison.OrdinalIgnoreCase))
            .Where(e =>
            {
                var parts = e.Key!.Replace('\\', '/').TrimStart('/').Split('/');
                return !parts.Any(p => p.StartsWith('.') || p.StartsWith('_'));
            })
            .ToList();
    }
}
