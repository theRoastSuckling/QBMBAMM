using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QBModsBrowser.Server.Models;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Scans local mods and builds lookup indexes so server features can map installed content.
public partial class LocalModService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _log;
    private readonly Func<string> _getModsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<LocalMod>? _cache;
    private Dictionary<string, LocalMod>? _byModId;
    private Dictionary<int, LocalMod>? _byTopicId;
    private Dictionary<int, List<LocalMod>>? _byTopicIdMulti;
    private Dictionary<int, LocalMod>? _byNexusId;

    // Creates the service with logger and deferred mods directory resolution.
    public LocalModService(ILogger logger, Func<string> getModsPath)
    {
        _log = logger.ForContext<LocalModService>();
        _getModsPath = getModsPath;
    }

    // Returns last scanned local mods to avoid repeated disk reads.
    public List<LocalMod> GetCachedMods() => _cache ?? [];

    // Returns local mods indexed by mod id for direct installation lookups.
    public Dictionary<string, LocalMod> GetByModIdIndex() => _byModId ?? new();

    // Returns local mods indexed by forum topic id for topic-based matching.
    public Dictionary<int, LocalMod> GetByTopicIdIndex() => _byTopicId ?? new();

    // Returns local mods grouped by forum topic id for one-to-many topic matching.
    public Dictionary<int, List<LocalMod>> GetByTopicIdMultiIndex() => _byTopicIdMulti ?? new();

    // Returns local mods indexed by Nexus id as fallback for ambiguous topics.
    public Dictionary<int, LocalMod> GetByNexusIdIndex() => _byNexusId ?? new();

    // Reverse-lookup: returns the forum topic id for a mod id, or null when not mapped.
    public int? GetTopicIdForModId(string modId)
    {
        if (_byTopicIdMulti == null) return null;
        foreach (var (topicId, mods) in _byTopicIdMulti)
        {
            if (mods.Any(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)))
                return topicId;
        }
        return null;
    }

    // Runs an initial scan once so downstream requests have local mod data.
    public async Task EnsureScannedAsync()
    {
        if (_cache != null) return;
        await ScanAsync();
    }

    /// <summary>Merges freshly installed mod folders into the cache so matching sees them without a full rescan.</summary>
    // Refreshes only recently installed folders so cache updates without full rescan.
    public async Task RefreshModsAtPathsAsync(IReadOnlyList<string> absoluteFolderPaths)
    {
        if (absoluteFolderPaths == null || absoluteFolderPaths.Count == 0) return;

        await EnsureScannedAsync();

        await _lock.WaitAsync();
        try
        {
            var modsPath = _getModsPath();
            var enabledModIds = await ReadEnabledModIdsAsync(modsPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in absoluteFolderPaths)
            {
                var folderPath = Path.GetFullPath(raw);
                if (!seen.Add(folderPath) || !Directory.Exists(folderPath)) continue;

                var mod = await ScanModFolder(folderPath, enabledModIds);
                if (mod == null) continue;

                _cache!.RemoveAll(m =>
                    string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));

                _cache!.Add(mod);
            }

            RebuildIndexes();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Full scan of mods directory used on startup or manual refresh actions.
    public async Task<List<LocalMod>> ScanAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var modsPath = _getModsPath();
            if (!Directory.Exists(modsPath))
            {
                _log.Warning("Mods path does not exist: {Path}", modsPath);
                _cache = [];
                RebuildIndexes();
                return _cache;
            }

            var mods = new List<LocalMod>();
            var enabledModIds = await ReadEnabledModIdsAsync(modsPath);
            var dirs = Directory.GetDirectories(modsPath);

            foreach (var dir in dirs)
            {
                try
                {
                    var mod = await ScanModFolder(dir, enabledModIds);
                    if (mod != null)
                        mods.Add(mod);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to scan mod folder: {Dir}", Path.GetFileName(dir));
                }
            }

            _cache = mods;
            RebuildIndexes();
            _log.Information("Local mod scan complete: {Count} mods found in {Path}", mods.Count, modsPath);
            return mods;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Parses one mod folder and returns normalized local mod metadata if valid.
    private async Task<LocalMod?> ScanModFolder(string folderPath, HashSet<string> enabledModIds)
    {
        var folderName = Path.GetFileName(folderPath);

        // Finds the canonical mod_info.json and derives enabled state from enabled_mods.json.
        var modInfoPath = FindModInfoFile(folderPath);
        if (modInfoPath == null)
            return null;

        var raw = await File.ReadAllTextAsync(modInfoPath);
        var json = JsonFixHelper.FixJson(raw);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to parse mod_info.json in {Folder}", folderName);
            return null;
        }

        if (node == null) return null;

        var modId = node["id"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(modId))
        {
            _log.Warning("mod_info.json missing 'id' in {Folder}", folderName);
            return null;
        }

        var mod = new LocalMod
        {
            ModId = modId,
            Name = node["name"]?.GetValue<string>() ?? modId,
            Author = node["author"]?.GetValue<string>(),
            Version = ExtractVersionString(node["version"]),
            GameVersion = node["gameVersion"]?.GetValue<string>(),
            FolderName = folderName,
            FolderPath = folderPath,
            IsEnabled = enabledModIds.Contains(modId),
            IsUtility = GetBoolSafe(node["utility"]),
            IsTotalConversion = GetBoolSafe(node["totalConversion"]),
            Description = node["description"]?.GetValue<string>()
        };

        // Parse dependencies (entries may be plain mod id strings or objects; version may be string or semver object).
        var depsNode = node["dependencies"];
        if (depsNode is JsonArray depsArr)
        {
            mod.Dependencies = [];
            foreach (var dep in depsArr)
            {
                if (dep == null) continue;

                if (dep is JsonValue)
                {
                    var idOnly = dep.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(idOnly))
                        mod.Dependencies.Add(new ModDependency { Id = idOnly });
                    continue;
                }

                if (dep is not JsonObject) continue;

                mod.Dependencies.Add(new ModDependency
                {
                    Id = dep["id"]?.GetValue<string>(),
                    Name = dep["name"]?.GetValue<string>(),
                    Version = ExtractVersionString(dep["version"])
                });
            }
        }

        // Discover and parse version checker info
        mod.VersionChecker = await LoadVersionCheckerInfo(folderPath);

        return mod;
    }

    // Reads version checker files so installed mods can be compared against remote versions.
    private async Task<VersionCheckerInfo?> LoadVersionCheckerInfo(string modFolder)
    {
        var csvPath = Path.Combine(modFolder, "data", "config", "version", "version_files.csv");
        if (!File.Exists(csvPath))
            return null;

        try
        {
            var csvLines = await File.ReadAllLinesAsync(csvPath);
            if (csvLines.Length < 2)
                return null;

            // Second row, first column is the relative path to the .version file
            var relativePath = csvLines[1].Split(',')[0].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            var versionFilePath = Path.Combine(modFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(versionFilePath))
                return null;

            var raw = await File.ReadAllTextAsync(versionFilePath);
            var json = JsonFixHelper.FixJson(raw);

            var node = JsonNode.Parse(json);
            if (node == null) return null;

            var info = new VersionCheckerInfo
            {
                ModName = node["modName"]?.GetValue<string>(),
                MasterVersionFile = node["masterVersionFile"]?.GetValue<string>(),
                DirectDownloadURL = node["directDownloadURL"]?.GetValue<string>(),
                ChangelogURL = node["changelogURL"]?.GetValue<string>()
            };

            // Parse modThreadId (strip non-digits)
            var rawThreadId = node["modThreadId"]?.ToString();
            if (!string.IsNullOrEmpty(rawThreadId))
            {
                var stripped = NonDigitRegex().Replace(rawThreadId, "");
                if (!string.IsNullOrEmpty(stripped) && stripped != "0" && !stripped.All(c => c == '0'))
                    info.ModThreadId = stripped;
            }

            // Parse modNexusId
            var rawNexusId = node["modNexusId"]?.ToString();
            if (!string.IsNullOrEmpty(rawNexusId))
            {
                var stripped = NonDigitRegex().Replace(rawNexusId, "");
                if (!string.IsNullOrEmpty(stripped))
                    info.ModNexusId = stripped;
            }

            // Parse modVersion object
            var versionNode = node["modVersion"];
            if (versionNode != null)
            {
                info.ModVersion = new VersionObject
                {
                    Major = versionNode["major"]?.ToString(),
                    Minor = versionNode["minor"]?.ToString(),
                    Patch = versionNode["patch"]?.ToString()
                };
            }

            return info;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load version checker for {Folder}", Path.GetFileName(modFolder));
            return null;
        }
    }

    // Locates the canonical mod_info.json file in a mod folder.
    private static string? FindModInfoFile(string folderPath)
    {
        var enabledPath = Path.Combine(folderPath, "mod_info.json");
        if (File.Exists(enabledPath))
            return enabledPath;

        return null;
    }

    // Reads enabled_mods.json into a case-insensitive id set used during local scan.
    private static async Task<HashSet<string>> ReadEnabledModIdsAsync(string modsPath)
    {
        var enabledModsPath = Path.Combine(modsPath, "enabled_mods.json");
        if (!File.Exists(enabledModsPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = await File.ReadAllTextAsync(enabledModsPath);
            var node = JsonNode.Parse(raw);
            var enabled = node?["enabledMods"] as JsonArray;
            if (enabled == null)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var ids = enabled
                .Select(x => x?.GetValue<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>();
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Normalizes version fields that may be strings or object components.
    private static string? ExtractVersionString(JsonNode? versionNode)
    {
        if (versionNode == null) return null;

        // Version can be a string like "1.4.2" or an object like { major: 1, minor: 4, patch: 2 }
        if (versionNode is JsonValue val)
        {
            return val.ToString();
        }

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

    // Recreates all lookup dictionaries after scan or targeted refresh changes.
    private void RebuildIndexes()
    {
        var mods = _cache ?? [];
        _byModId = new(StringComparer.OrdinalIgnoreCase);
        _byTopicId = new();
        _byTopicIdMulti = new();
        _byNexusId = new();

        foreach (var mod in mods)
        {
            _byModId.TryAdd(mod.ModId, mod);

            if (mod.VersionChecker?.ModThreadId is string tid && int.TryParse(tid, out int topicId) && topicId > 0)
            {
                if (!_byTopicIdMulti.TryGetValue(topicId, out var group))
                {
                    group = [];
                    _byTopicIdMulti[topicId] = group;
                }

                group.Add(mod);
                _byTopicId.TryAdd(topicId, mod);
            }

            if (mod.VersionChecker?.ModNexusId is string nid && int.TryParse(nid, out int nexusId) && nexusId > 0)
                _byNexusId.TryAdd(nexusId, mod);
        }
    }

    // Safely parses loose boolean values found in inconsistent mod metadata.
    private static bool GetBoolSafe(JsonNode? node)
    {
        if (node == null) return false;
        try { return node.GetValue<bool>(); }
        catch
        {
            var str = node.ToString();
            return str.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Strips non-digit characters from checker ids before numeric parsing.
    [GeneratedRegex(@"\D", RegexOptions.Compiled)]
    private static partial Regex NonDigitRegex();
}
