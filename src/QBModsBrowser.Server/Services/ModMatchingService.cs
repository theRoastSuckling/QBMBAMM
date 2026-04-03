using System.Text.Json;
using System.Text.RegularExpressions;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Utilities;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Merges scraped forum mods with repo/local install data so UI can show install/update state.
public partial class ModMatchingService
{
    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;

    private readonly ILogger _log;
    private readonly JsonDataStore _store;
    private readonly ModRepoService _modRepo;
    private readonly LocalModService _localMods;
    private readonly VersionCheckerService _versionChecker;
    private readonly DownloadManager _downloads;
    private readonly string _matchesPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, int>? _persistedMatches;

    // Wires dependencies required to enrich mod lists and persist learned matches.
    public ModMatchingService(
        ILogger logger,
        JsonDataStore store,
        ModRepoService modRepo,
        LocalModService localMods,
        VersionCheckerService versionChecker,
        DownloadManager downloads,
        string dataPath)
    {
        _log = logger.ForContext<ModMatchingService>();
        _store = store;
        _modRepo = modRepo;
        _localMods = localMods;
        _versionChecker = versionChecker;
        _downloads = downloads;
        _matchesPath = Path.Combine(dataPath, "mod-matches.json");
    }

    // Preloads persisted matches and required caches at startup.
    public async Task InitializeAsync()
    {
        await LoadPersistedMatches();
        await _modRepo.GetDataAsync();
        await _localMods.EnsureScannedAsync();
    }

    // Ensures all matching inputs are loaded before an enrichment run.
    public async Task PrepareEnrichmentAsync()
    {
        if (_persistedMatches == null) await LoadPersistedMatches();
        await _modRepo.GetDataAsync();
        await _localMods.EnsureScannedAsync();
    }

    // Async entry point used by controllers to enrich scraped summary lists.
    public async Task<List<EnrichedModSummary>> EnrichAsync(List<ModSummary> summaries)
    {
        await PrepareEnrichmentAsync();
        return Enrich(summaries);
    }

    /// <summary>
    /// Enriches the given list of ModSummary objects with ModRepo + local mod data.
    /// </summary>
    // Combines repo, local, and cached-download signals into enriched mod cards.
    public List<EnrichedModSummary> Enrich(List<ModSummary> summaries)
    {
        var topicIndex = _modRepo.GetTopicIndex();
        var localByTopicIdMulti = _localMods.GetByTopicIdMultiIndex();
        var localByNexusId = _localMods.GetByNexusIdIndex();
        var localByModId = _localMods.GetByModIdIndex();
        var allLocalMods = _localMods.GetCachedMods();
        var persisted = _persistedMatches ?? new();

        var result = new List<EnrichedModSummary>(summaries.Count);

        foreach (var s in summaries)
        {
            var enriched = EnrichedModSummary.FromSummary(s);
            var topicLocalMods = localByTopicIdMulti.TryGetValue(s.TopicId, out var groupedByTopic)
                ? groupedByTopic
                : [];

            // Step 1: ModRepo enrichment via topic ID
            if (topicIndex.TryGetValue(s.TopicId, out var repoEntry))
            {
                enriched.ModRepoName = repoEntry.Name;
                enriched.ModRepoVersion = repoEntry.ModVersion;

                var ddUrl = repoEntry.GetUrl("DirectDownload");
                // Skip hosts the app cannot auto-download from (e.g. mega.nz).
                if (!string.IsNullOrEmpty(ddUrl) && !UrlNormalizer.IsUnsupportedAutoDownloadHost(ddUrl))
                    enriched.DirectDownloadUrl = ddUrl;

                var dpUrl = repoEntry.GetUrl("DownloadPage");
                if (!string.IsNullOrEmpty(dpUrl))
                    enriched.DownloadPageUrl = dpUrl;

                var selectedImageUrl = GetFirstModRepoImageUrl(repoEntry);
                if (!string.IsNullOrWhiteSpace(selectedImageUrl))
                    enriched.ThumbnailPath = $"ext:{selectedImageUrl}";
            }

            // Step 2: Local mod matching
            LocalMod? localMod = null;

            // Try topic-ID-based match (from local version checker)
            if (topicLocalMods.Count > 0)
                localMod = topicLocalMods[0];

            // Try persisted match
            if (localMod == null)
            {
                var matchedModId = persisted
                    .Where(kv => kv.Value == s.TopicId)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (matchedModId != null && localByModId.TryGetValue(matchedModId, out var byPersisted))
                    localMod = byPersisted;
            }

            // Try nexus-based match
            if (localMod == null && repoEntry != null)
            {
                var nexusUrl = repoEntry.GetUrl("NexusMods");
                if (!string.IsNullOrEmpty(nexusUrl))
                {
                    var match = NexusIdRegex().Match(nexusUrl);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int nid))
                    {
                        localByNexusId.TryGetValue(nid, out localMod);
                    }
                }
            }

            if (localMod != null)
            {
                enriched.IsInstalled = true;
                enriched.IsEnabled = localMod.IsEnabled;
                enriched.LocalModId = localMod.ModId;
                enriched.LocalModName = string.IsNullOrWhiteSpace(localMod.Name) ? null : localMod.Name;
                enriched.LocalVersion = localMod.Version;
                enriched.LocalGameVersion = localMod.GameVersion;

                // Persist all known local mods that explicitly point at this topic, not just the first one.
                SaveMatchIfNew(localMod.ModId, s.TopicId);
                foreach (var topicLocal in topicLocalMods)
                {
                    if (string.IsNullOrWhiteSpace(topicLocal.ModId)) continue;
                    if (string.Equals(topicLocal.ModId, localMod.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                    SaveMatchIfNew(topicLocal.ModId, s.TopicId);
                }

                // Version check data
                var vcResult = _versionChecker.GetCachedResult(localMod.ModId);
                if (vcResult != null)
                {
                    enriched.OnlineVersion = vcResult.RemoteVersion?.ToString();
                    enriched.UpdateAvailable = vcResult.UpdateAvailable;
                    // Skip hosts the app cannot auto-download from (e.g. mega.nz).
                    if (!string.IsNullOrEmpty(vcResult.DirectDownloadUrl) && !UrlNormalizer.IsUnsupportedAutoDownloadHost(vcResult.DirectDownloadUrl))
                        enriched.UpdateDownloadUrl = vcResult.DirectDownloadUrl;
                }
            }

            // Include additional local mods that map to the same forum topic.
            var siblingTopicMods = topicLocalMods
                .Where(m => localMod == null || !string.Equals(m.ModId, localMod.ModId, StringComparison.OrdinalIgnoreCase))
                .Select(m => new LocalModInfo
                {
                    ModId = m.ModId,
                    Name = m.Name,
                    Version = m.Version,
                    GameVersion = m.GameVersion,
                    IsEnabled = m.IsEnabled
                })
                .ToList();
            if (siblingTopicMods.Count > 0)
                MergeAdditionalLocalMods(enriched, siblingTopicMods);

            // Determine if direct download is available
            enriched.HasDirectDownload = !string.IsNullOrEmpty(enriched.DirectDownloadUrl)
                || !string.IsNullOrEmpty(enriched.UpdateDownloadUrl);

            // Multi-mod: find additional local mods from the topic-archive map
            var archiveMap = _downloads.GetTopicArchiveMap();
            if (archiveMap.TryGetValue(s.TopicId, out var archiveEntries))
            {
                var additionalMods = new List<LocalModInfo>();
                var addedExtraIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ae in archiveEntries)
                {
                    foreach (var modId in ae.ModIds)
                    {
                        if (localMod != null && string.Equals(modId, localMod.ModId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (localByModId.TryGetValue(modId, out var extra))
                        {
                            if (!enriched.IsInstalled)
                            {
                                enriched.IsInstalled = true;
                                enriched.IsEnabled = extra.IsEnabled;
                                enriched.LocalModId = extra.ModId;
                                enriched.LocalModName = extra.Name;
                                enriched.LocalVersion = extra.Version;
                                enriched.LocalGameVersion = extra.GameVersion;
                                continue; // Primary match for this topic; do not also add as extra.
                            }

                            if (!string.IsNullOrWhiteSpace(enriched.LocalModId)
                                && string.Equals(extra.ModId, enriched.LocalModId, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (addedExtraIds.Add(extra.ModId))
                            {
                                additionalMods.Add(new LocalModInfo
                                {
                                    ModId = extra.ModId,
                                    Name = extra.Name,
                                    Version = extra.Version,
                                    GameVersion = extra.GameVersion,
                                    IsEnabled = extra.IsEnabled
                                });
                            }
                        }
                    }
                }
                if (additionalMods.Count > 0)
                    enriched.AdditionalLocalMods = additionalMods;
            }

            // Fallback for manually installed variants: infer additional matches by stable base name.
            var inferredAdditional = FindAdditionalLocalModsByName(s, enriched, allLocalMods);
            if (inferredAdditional.Count > 0)
                MergeAdditionalLocalMods(enriched, inferredAdditional);

            result.Add(enriched);
        }

        return result;
    }

    /// <summary>
    /// Returns the set of topic IDs that correspond to installed local mods.
    /// Used to bypass version filters for installed mods.
    /// </summary>
    // Returns installed topic ids so filters keep locally installed mods visible.
    public HashSet<int> GetInstalledTopicIds()
    {
        var result = new HashSet<int>();
        var localByTopicIdMulti = _localMods.GetByTopicIdMultiIndex();
        foreach (var tid in localByTopicIdMulti.Keys)
            result.Add(tid);

        var persisted = _persistedMatches ?? new();
        foreach (var kv in persisted)
        {
            if (_localMods.GetByModIdIndex().ContainsKey(kv.Key))
                result.Add(kv.Value);
        }

        return result;
    }

    // Returns persisted mod-id-to-topic-id entries that point to the given topic.
    public Dictionary<string, int> GetPersistedMatchesForTopic(int topicId)
    {
        var all = _persistedMatches ?? new();
        return all.Where(kv => kv.Value == topicId)
                  .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // Exposes the full persisted matches map for dependency resolution lookups.
    public IReadOnlyDictionary<string, int> GetPersistedMatches() =>
        _persistedMatches ?? new Dictionary<string, int>();

    /// <summary>
    /// Returns local mods that have no forum topic match (for "unknown" section).
    /// </summary>
    // Returns local mods not mapped to known topics for unknown/local-only sections.
    public List<LocalMod> GetUnmatchedLocalMods()
    {
        var localMods = _localMods.GetCachedMods();
        var localByTopicIdMulti = _localMods.GetByTopicIdMultiIndex();
        var persisted = _persistedMatches ?? new();
        var matchedModIds = new HashSet<string>(
            localByTopicIdMulti.Values.SelectMany(group => group).Select(m => m.ModId),
            StringComparer.OrdinalIgnoreCase);
        matchedModIds.UnionWith(persisted.Keys);

        return localMods.Where(m => !matchedModIds.Contains(m.ModId)).ToList();
    }

    /// <summary>
    /// Persisted "downloaded from this topic" hints. Google Drive entries whose file id does not appear in
    /// non-spoiler post links are omitted (e.g. skin packs only linked inside spoilers).
    /// </summary>
    // Returns remembered download archives per topic, filtered against non-spoiler links.
    public List<TopicArchiveEntry> GetTopicArchiveEntries(int topicId, ModDetail? detail = null)
    {
        var archiveMap = _downloads.GetTopicArchiveMap();
        if (!archiveMap.TryGetValue(topicId, out var entries) || entries == null || entries.Count == 0)
            return [];

        if (detail == null || string.IsNullOrWhiteSpace(detail.ContentHtml))
            return [.. entries];

        var filteredLinks = SpoilerLinkFilter.FilterOutSpoilerLinks(detail.ContentHtml, detail.Links);
        var result = new List<TopicArchiveEntry>(entries.Count);
        foreach (var e in entries)
        {
            var gid = TryExtractGoogleDriveFileId(e.DownloadUrl);
            if (gid != null && !LinkSetContainsDriveFileId(gid, filteredLinks))
                continue;
            result.Add(e);
        }

        return result;
    }

    // Extracts Google Drive file ids so stored archives can be validated against links.
    private static string? TryExtractGoogleDriveFileId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"[?&]id=([^&]+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"/file/d/([^/]+)/", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Checks if a filtered link set still references a specific Google Drive file id.
    private static bool LinkSetContainsDriveFileId(string fileId, List<LinkRef> links)
    {
        foreach (var l in links)
        {
            if (string.IsNullOrEmpty(l.Url)) continue;
            if (l.Url.Contains(fileId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Stores newly discovered local->topic matches to improve future matching accuracy.
    private void SaveMatchIfNew(string modId, int topicId)
    {
        var matches = _persistedMatches ??= new();
        if (matches.TryGetValue(modId, out int existing) && existing == topicId)
            return;

        matches[modId] = topicId;
        _ = PersistMatchesAsync();
    }

    // Persists learned matches to disk so future runs start with known mappings.
    private async Task PersistMatchesAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_persistedMatches ?? new(), JsonOpts);
            await File.WriteAllTextAsync(_matchesPath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to persist mod matches");
        }
    }

    // Loads match hints from disk at startup or first enrichment request.
    private async Task LoadPersistedMatches()
    {
        if (!File.Exists(_matchesPath))
        {
            _persistedMatches = new();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_matchesPath);
            _persistedMatches = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOpts) ?? new();
            _log.Information("Loaded {Count} persisted mod matches", _persistedMatches.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load persisted mod matches");
            _persistedMatches = new();
        }
    }

    // Parses Nexus mod ids from repo links used as a fallback local matcher.
    [GeneratedRegex(@"/mods/(\d+)", RegexOptions.Compiled)]
    private static partial Regex NexusIdRegex();

    // Picks the first usable image URL for lightweight thumbnail selection.
    private static string? GetFirstModRepoImageUrl(ModRepoEntry entry)
    {
        if (entry.Images == null || entry.Images.Count == 0)
            return null;

        foreach (var img in entry.Images.Values)
        {
            if (!string.IsNullOrWhiteSpace(img.ProxyUrl))
                return img.ProxyUrl;
            if (!string.IsNullOrWhiteSpace(img.Url))
                return img.Url;
        }

        return null;
    }

    // Adds inferred additional local mods while keeping ids unique in the enriched payload.
    private static void MergeAdditionalLocalMods(EnrichedModSummary enriched, List<LocalModInfo> inferred)
    {
        var merged = enriched.AdditionalLocalMods != null
            ? new List<LocalModInfo>(enriched.AdditionalLocalMods)
            : [];
        var seen = new HashSet<string>(
            merged.Select(m => m.ModId).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var info in inferred)
        {
            if (string.IsNullOrWhiteSpace(info.ModId)) continue;
            if (!seen.Add(info.ModId)) continue;
            merged.Add(info);
        }

        if (merged.Count > 0)
            enriched.AdditionalLocalMods = merged;
    }

    // Infers extra local mods for a topic using strict base-name matching for manual installs.
    private static List<LocalModInfo> FindAdditionalLocalModsByName(
        ModSummary summary,
        EnrichedModSummary enriched,
        IReadOnlyList<LocalMod> allLocalMods)
    {
        if (allLocalMods.Count == 0) return [];

        var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBaseName(baseNames, summary.Title);
        AddBaseName(baseNames, enriched.ModRepoName);
        AddBaseName(baseNames, enriched.LocalModName);
        if (baseNames.Count == 0) return [];

        var alreadyLinked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(enriched.LocalModId))
            alreadyLinked.Add(enriched.LocalModId);
        if (enriched.AdditionalLocalMods != null)
        {
            foreach (var m in enriched.AdditionalLocalMods)
            {
                if (!string.IsNullOrWhiteSpace(m.ModId))
                    alreadyLinked.Add(m.ModId);
            }
        }

        var inferred = new List<LocalModInfo>();
        foreach (var local in allLocalMods)
        {
            if (string.IsNullOrWhiteSpace(local.ModId)) continue;
            if (alreadyLinked.Contains(local.ModId)) continue;

            if (int.TryParse(local.VersionChecker?.ModThreadId, out var mappedTopicId)
                && mappedTopicId > 0
                && mappedTopicId != summary.TopicId)
            {
                continue;
            }

            var localBaseName = ToBaseName(local.Name);
            var localBaseFolder = ToBaseName(local.FolderName);
            var isMatch = baseNames.Contains(localBaseName) || baseNames.Contains(localBaseFolder);
            if (!isMatch) continue;

            inferred.Add(new LocalModInfo
            {
                ModId = local.ModId,
                Name = local.Name,
                Version = local.Version,
                GameVersion = local.GameVersion,
                IsEnabled = local.IsEnabled
            });
            alreadyLinked.Add(local.ModId);
        }

        return inferred;
    }

    // Adds a normalized base-name seed used for strict cross-source mod-name matching.
    private static void AddBaseName(HashSet<string> set, string? value)
    {
        var normalized = ToBaseName(value);
        if (!string.IsNullOrWhiteSpace(normalized))
            set.Add(normalized);
    }

    // Normalizes names so forum titles and local folder variants can match reliably.
    private static string ToBaseName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var s = raw.Trim();
        s = Regex.Replace(s, @"^\s*(?:Re:\s*)?(?:\[[^\]]+\]\s*)*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*[-_]\s*(?:v(?:ersion)?\s*)?\d+(?:\.\d+)*\s*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @"[^A-Za-z0-9 ]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        return s.Length >= 6 ? s : "";
    }
}
