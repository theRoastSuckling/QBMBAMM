using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;
using QBModsBrowser.Server.Utilities;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/mods")]
// Serves the linkage-report endpoint that exposes all raw source data for a single topic.
public class LinkageReportController : ControllerBase
{
    private readonly JsonDataStore _store;
    private readonly ModMatchingService _matching;
    private readonly AssumedDownloadService _assumed;
    private readonly ModRepoService _modRepo;
    private readonly LocalModService _localMods;
    private readonly VersionCheckerService _versionChecker;
    private readonly DownloadManager _downloads;
    private readonly ManagerConfig _managerConfig;
    private readonly IConfiguration _config;

    // Injects all services required to aggregate every data source for a topic.
    public LinkageReportController(
        JsonDataStore store,
        ModMatchingService matching,
        AssumedDownloadService assumed,
        ModRepoService modRepo,
        LocalModService localMods,
        VersionCheckerService versionChecker,
        DownloadManager downloads,
        ManagerConfig managerConfig,
        IConfiguration config)
    {
        _store = store;
        _matching = matching;
        _assumed = assumed;
        _modRepo = modRepo;
        _localMods = localMods;
        _versionChecker = versionChecker;
        _downloads = downloads;
        _managerConfig = managerConfig;
        _config = config;
    }

    // Returns raw data from every data source for one topic, showing connections and storage paths.
    [HttpGet("{id:int}/linkage-report")]
    public async Task<IActionResult> GetDataLinkage(int id)
    {
        var dataPath = _config["ResolvedDataPath"] ?? "data";
        var modsPath = _managerConfig.ModsPath;

        // 1. Forum scrape
        var index = await _store.LoadIndex();
        var summary = index.FirstOrDefault(m => m.TopicId == id);
        var detail = await _store.LoadDetail(id);

        object? forumIndex = summary != null ? new
        {
            summary.TopicId, summary.Title, summary.Category, summary.InModIndex,
            summary.IsArchivedModIndex, summary.GameVersion, summary.Author,
            summary.Replies, summary.Views, summary.CreatedDate,
            summary.LastPostDate, summary.LastPostBy, summary.TopicUrl,
            summary.ThumbnailPath, summary.ScrapedAt, summary.SourceBoard,
            scrapedAtDisplay = FormatHelper.UtcToLocalDateTime(summary.ScrapedAt)
        } : null;

        object? forumDetail = detail != null ? new
        {
            detail.TopicId, detail.Title, detail.Category, detail.GameVersion,
            detail.Author, detail.AuthorTitle, detail.PostDate, detail.LastEditDate,
            detail.ScrapedAt, detail.IsPlaceholderDetail,
            imageCount = detail.Images.Count,
            linkCount = detail.Links.Count,
            links = detail.Links.Select(l => new { l.Url, l.Text, l.IsExternal })
        } : null;

        // 2. ModRepo
        var repoEntry = _modRepo.FindByTopicId(id);
        object? modRepo = repoEntry != null ? new
        {
            repoEntry.Name, repoEntry.Summary, repoEntry.ModVersion,
            repoEntry.GameVersionReq, repoEntry.AuthorsList, repoEntry.Categories,
            repoEntry.Sources, repoEntry.Urls, repoEntry.DateTimeCreated,
            repoEntry.DateTimeEdited
        } : null;

        // 3. Local mods matched to this topic
        await _localMods.EnsureScannedAsync();
        var localByTopicMulti = _localMods.GetByTopicIdMultiIndex();
        var localByModId = _localMods.GetByModIdIndex();
        var localByNexusId = _localMods.GetByNexusIdIndex();

        var matchedLocals = new List<object>();
        var seenModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match via VersionChecker ModThreadId
        if (localByTopicMulti.TryGetValue(id, out var topicGroup))
        {
            foreach (var lm in topicGroup)
            {
                if (!seenModIds.Add(lm.ModId)) continue;
                matchedLocals.Add(BuildLocalModPayload(lm, "VersionChecker.ModThreadId"));
            }
        }

        // Match via persisted mod-matches.json
        var persistedMatches = _matching.GetPersistedMatchesForTopic(id);
        foreach (var kv in persistedMatches)
        {
            if (seenModIds.Contains(kv.Key)) continue;
            if (localByModId.TryGetValue(kv.Key, out var lm))
            {
                seenModIds.Add(lm.ModId);
                matchedLocals.Add(BuildLocalModPayload(lm, "Persisted mod-matches.json"));
            }
        }

        // Match via NexusId cross-reference
        if (repoEntry != null)
        {
            var nexusUrl = repoEntry.GetUrl("NexusMods");
            if (!string.IsNullOrEmpty(nexusUrl))
            {
                var nMatch = System.Text.RegularExpressions.Regex.Match(nexusUrl, @"/mods/(\d+)");
                if (nMatch.Success && int.TryParse(nMatch.Groups[1].Value, out int nid)
                    && localByNexusId.TryGetValue(nid, out var nexusMod)
                    && seenModIds.Add(nexusMod.ModId))
                {
                    matchedLocals.Add(BuildLocalModPayload(nexusMod, $"NexusId {nid} via ModRepo URL"));
                }
            }
        }

        // 4. Version checker results for each matched local mod
        var versionChecks = new List<object>();
        foreach (var modIdStr in seenModIds)
        {
            var vcResult = _versionChecker.GetCachedResult(modIdStr);
            if (vcResult != null)
            {
                versionChecks.Add(new
                {
                    vcResult.ModId,
                    localVersion = vcResult.LocalVersion?.ToString(),
                    remoteVersion = vcResult.RemoteVersion?.ToString(),
                    vcResult.UpdateAvailable,
                    vcResult.DirectDownloadUrl,
                    vcResult.Error,
                    vcResult.CheckedAt
                });
            }
        }

        // 5. Topic-archive map
        var archiveMap = _downloads.GetTopicArchiveMap();
        archiveMap.TryGetValue(id, out var archiveEntries);

        var archivePayload = (archiveEntries ?? []).Select(ae =>
        {
            var installedMods = ae.ModIds
                .Where(mid => localByModId.ContainsKey(mid))
                .Select(mid => localByModId[mid].Name)
                .ToList();
            return new
            {
                ae.ArchiveName,
                ae.DownloadUrl,
                ae.ModIds,
                downloadedViaQB = true,
                installedModNames = installedMods
            };
        }).ToList();

        // 6. Mod matches (persisted) — already have persistedMatches from step 3

        // 7. Assumed downloads
        var assumedCandidates = _assumed.GetCachedCandidates(id);

        // Build storage paths
        var detailPath = Path.Combine(dataPath, "mods", id.ToString(), "detail.json");
        var detailFileExists = System.IO.File.Exists(detailPath);

        return Ok(new
        {
            topicId = id,
            forumUrl = $"https://fractalsoftworks.com/forum/index.php?topic={id}.0",

            forumScrape = new
            {
                exists = summary != null || detail != null,
                storagePaths = new
                {
                    index = Path.GetFullPath(Path.Combine(dataPath, "mods-index.json")),
                    detail = detailFileExists ? Path.GetFullPath(detailPath) : null
                },
                connectionMethod = "Direct topicId field in scraped data",
                indexData = forumIndex,
                detailData = forumDetail
            },

            modRepo = new
            {
                exists = repoEntry != null,
                storagePath = Path.GetFullPath(Path.Combine(dataPath, "mod-repo-cache.json")),
                connectionMethod = "Urls.Forum parsed for ?topic= parameter",
                sourceUrl = "https://github.com/wispborne/StarsectorModRepo/raw/refs/heads/main/ModRepo.json",
                data = modRepo
            },

            localMods = new
            {
                exists = matchedLocals.Count > 0,
                storagePath = modsPath,
                connectionMethods = new[]
                {
                    "VersionChecker.ModThreadId inside .version file",
                    "Persisted mod-matches.json (modId -> topicId)",
                    "NexusId cross-reference via ModRepo URL"
                },
                data = matchedLocals
            },

            versionChecker = new
            {
                exists = versionChecks.Count > 0,
                storagePath = "*.version files inside each mod's data/config/version/ folder",
                connectionMethod = "Embedded in local mod folder; modThreadId links back to topic",
                data = versionChecks
            },

            topicArchiveMap = new
            {
                exists = archiveEntries != null && archiveEntries.Count > 0,
                storagePath = Path.GetFullPath(Path.Combine(dataPath, "topic-archive-map.json")),
                connectionMethod = "Key is topicId; modIds[] link to local mod folders",
                data = archivePayload
            },

            modMatches = new
            {
                exists = persistedMatches.Count > 0,
                storagePath = Path.GetFullPath(Path.Combine(dataPath, "mod-matches.json")),
                connectionMethod = "Auto-saved when an installed local mod is matched to a forum topic while browsing. Independent of QBMBAMM download history.",
                data = persistedMatches
            },

            assumedDownloads = new
            {
                exists = assumedCandidates != null && assumedCandidates.Count > 0,
                storagePath = Path.GetFullPath(Path.Combine(dataPath, "assumed-downloads-cache.json")),
                connectionMethod = "Resolved from forum post links; key is topicId",
                data = assumedCandidates
            }
        });
    }

    // Shapes one local mod into a linkage-friendly payload including version checker details.
    private static object BuildLocalModPayload(LocalMod lm, string matchMethod)
    {
        return new
        {
            matchMethod,
            lm.ModId,
            lm.Name,
            lm.Author,
            lm.Version,
            lm.GameVersion,
            lm.FolderName,
            lm.FolderPath,
            lm.IsEnabled,
            lm.IsUtility,
            lm.IsTotalConversion,
            lm.Description,
            dependencies = lm.Dependencies?.Select(d => new { d.Id, d.Name, d.Version }),
            versionChecker = lm.VersionChecker != null ? new
            {
                lm.VersionChecker.ModName,
                lm.VersionChecker.MasterVersionFile,
                modVersion = lm.VersionChecker.ModVersion?.ToString(),
                lm.VersionChecker.ModThreadId,
                lm.VersionChecker.ModNexusId,
                lm.VersionChecker.DirectDownloadURL,
                lm.VersionChecker.ChangelogURL
            } : null
        };
    }

}
