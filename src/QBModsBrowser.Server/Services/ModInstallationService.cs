using System.Text.Json.Nodes;
using QBModsBrowser.Server.Models;
using SharpCompress.Archives;
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

    /// <summary>
    /// Extracts and installs a mod from a downloaded archive into the mods folder.
    /// Handles both "folder zipped" and "loose files zipped" cases.
    /// </summary>
    // Installs one archive, supporting both packed-folder and loose-file archive layouts.
    public async Task<ModArchiveInstallResult> InstallFromArchiveAsync(
        string archivePath,
        string modsFolder,
        Action<long, long>? onExtractProgress = null)
    {
        if (archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Routes .7z files through a dedicated fast installer with SharpCompress fallback.
                return await SevenZipFastInstaller.InstallFromArchiveAsync(archivePath, modsFolder, _log, onExtractProgress);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Fast .7z install failed, falling back to SharpCompress");
            }
        }

        var result = new ModArchiveInstallResult();
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();

        // Find all mod_info.json files in the archive
        var modInfoEntries = GetValidModInfoEntries(entries);

        if (modInfoEntries.Count == 0)
            throw new InvalidOperationException("No mod_info.json found in archive");

        foreach (var modInfoEntry in modInfoEntries)
        {
            var modInfoPath = NormalizePath(modInfoEntry.Key!);
            var pathParts = modInfoPath.Split('/');

            string modRootPrefix;
            string targetFolderName;
            bool isLooseFiles;
            string? detectedModId = null;
            string? archiveVersion = null;

            if (pathParts.Length == 1)
            {
                isLooseFiles = true;
                modRootPrefix = "";
                (detectedModId, archiveVersion) = await ReadModInfoFromEntry(modInfoEntry);
                targetFolderName = detectedModId ?? Path.GetFileNameWithoutExtension(archivePath);
            }
            else
            {
                isLooseFiles = false;
                // Use the direct parent of mod_info.json as the mod root.
                // Handles wrapper-folder zips (e.g. PMMM/PMMM/ and PMMM/PMMMVE/) where pathParts[0]
                // is a shared wrapper, not an individual mod root.
                modRootPrefix = string.Join("/", pathParts[..^1]) + "/";
                targetFolderName = pathParts[^2];
                (detectedModId, archiveVersion) = await ReadModInfoFromEntry(modInfoEntry);
            }

            var targetPath = Path.Combine(modsFolder, targetFolderName);

            if (Directory.Exists(targetPath))
            {
                // Skip extraction when the installed version already matches the archive version.
                // Still record the mod ID so the topic-archive map is updated and the candidate gets linked.
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

                var backupPath = targetPath + ".backup_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try
                {
                    Directory.Move(targetPath, backupPath);
                    _log.Information("Backed up existing mod to {Backup}", Path.GetFileName(backupPath));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Could not backup existing mod, overwriting");
                    Directory.Delete(targetPath, true);
                }
            }

            Directory.CreateDirectory(targetPath);

            var extractableEntries = isLooseFiles
                ? entries.Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key)).ToList()
                : entries.Where(e =>
                    !e.IsDirectory
                    && !string.IsNullOrWhiteSpace(e.Key)
                    && NormalizePath(e.Key!).StartsWith(modRootPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var totalExtractable = extractableEntries.Count;
            long extracted = 0;
            onExtractProgress?.Invoke(extracted, totalExtractable);
            foreach (var entry in entries)
            {
                if (entry.Key == null || entry.IsDirectory) continue;

                var normalizedKey = NormalizePath(entry.Key);
                string relativePath;

                if (isLooseFiles)
                {
                    relativePath = normalizedKey;
                }
                else
                {
                    if (!normalizedKey.StartsWith(modRootPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    relativePath = normalizedKey[modRootPrefix.Length..];
                }

                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                var destPath = Path.Combine(targetPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir != null) Directory.CreateDirectory(destDir);

                await using var entryStream = entry.OpenEntryStream();
                await using var fileStream = File.Create(destPath);
                await entryStream.CopyToAsync(fileStream);
                extracted++;
                onExtractProgress?.Invoke(extracted, totalExtractable);
            }

            if (!string.IsNullOrWhiteSpace(detectedModId))
                result.ModIds.Add(detectedModId);

            result.InstalledFolderPaths.Add(Path.GetFullPath(targetPath));

            _log.Information("Installed mod {ModFolder} (id={ModId}): {Count} files extracted",
                targetFolderName, detectedModId ?? "unknown", extracted);
        }

        return result;
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

    // Reads mod id and version from an archive entry's mod_info.json in one pass.
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

    // Filters archive entries down to valid mod_info.json files used for id detection and installs.
    private static List<IArchiveEntry> GetValidModInfoEntries(List<IArchiveEntry> entries)
    {
        return entries
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .Where(e =>
            {
                var name = Path.GetFileName(e.Key!);
                return name.Equals("mod_info.json", StringComparison.OrdinalIgnoreCase);
            })
            .Where(e =>
            {
                // Ignore files inside hidden/special directories
                var parts = NormalizePath(e.Key!).Split('/');
                return !parts.Any(p => p.StartsWith('.') || p.StartsWith('_'));
            })
            .ToList();
    }

    // Normalizes archive internal paths for reliable cross-platform extraction logic.
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
