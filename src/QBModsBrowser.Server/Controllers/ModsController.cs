using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Scraper;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/mods")]
// Serves mod list/detail API endpoints with filtering, enrichment, and link analysis.
public class ModsController : ControllerBase
{
    private readonly JsonDataStore _store;
    private readonly ModMatchingService _matching;
    private readonly AssumedDownloadService _assumed;
    private readonly DependencyService _deps;
    private readonly ForumDataFetchService _forumFetch;

    // Creates the controller with datastore, matching, assumed-download, dependency, and fetch services.
    public ModsController(JsonDataStore store, ModMatchingService matching, AssumedDownloadService assumed, DependencyService deps, ForumDataFetchService forumFetch)
    {
        _store = store;
        _matching = matching;
        _assumed = assumed;
        _deps = deps;
        _forumFetch = forumFetch;
    }

    // Returns paged mod summaries with filters, sorting, and manager enrichment fields.
    // Pagination subtracts unmatched-dependency ghost slots from each page so the UI grid row count stays correct.
    [HttpGet]
    public async Task<IActionResult> GetMods(
        [FromQuery] string? search,
        [FromQuery] string? minVersion,
        [FromQuery] string? category,
        [FromQuery] bool modIndexOnly = false,
        [FromQuery] bool includeArchived = false,
        [FromQuery] string sort = "title",
        [FromQuery] string dir = "asc",
        [FromQuery] string age = "all",
        [FromQuery] string? topicIds = null,
        [FromQuery] string? installStatus = null,
        [FromQuery] bool favoritesFirst = false,
        [FromQuery] bool updatesFirst = false,
        [FromQuery] bool installedFirst = false,
        [FromQuery] bool installedCategoryFirst = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24)
    {
        // Trigger a remote data freshness check on each page load without blocking the response.
        _ = Task.Run(() => _forumFetch.EnsureDataFreshAsync());

        var mods = await _store.LoadIndex();
        await _matching.PrepareEnrichmentAsync();

        var allVersions = mods
            .Where(m => m.GameVersion != null)
            .Select(m => m.GameVersion!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, Comparer<string>.Create((a, b) => GameVersionComparer.Compare(a, b)))
            .ToList();
        var allCategories = mods
            .Select(m => NormalizeCategory(m.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            string q = search.Trim();
            mods = mods.Where(m =>
                m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        // Track installed topic IDs so they bypass the version filter
        var installedTopicIds = _matching.GetInstalledTopicIds();

        if (!string.IsNullOrWhiteSpace(minVersion))
        {
            string floor = minVersion.Trim();
            mods = mods.Where(m =>
                installedTopicIds.Contains(m.TopicId)
                || (!string.IsNullOrWhiteSpace(m.GameVersion)
                    ? GameVersionComparer.IsAtLeast(m.GameVersion, floor)
                    : ForumConstants.IsLibraryCategoryName(m.Category)
                      || ForumConstants.IsStandaloneUtilityCategoryName(m.Category))).ToList();
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            string wanted = category.Trim();
            mods = mods.Where(m => string.Equals(
                NormalizeCategory(m.Category),
                NormalizeCategory(wanted),
                StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Library board scrapes are never on the forum mod index (InModIndex=false). Still show them when
        // "mod index only" is on so they are not hidden from the main grid.
        if (modIndexOnly)
        {
            mods = mods.Where(m =>
                ForumConstants.IsLibraryCategoryName(m.Category)
                || (includeArchived
                    ? m.InModIndex
                    : m.InModIndex && !m.IsArchivedModIndex)).ToList();
        }

        var cutoff = GetCutoff(age);
        if (cutoff.HasValue)
        {
            mods = mods.Where(m => GetCreatedDate(m) >= cutoff.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(topicIds))
        {
            var idSet = new HashSet<int>();
            foreach (var part in topicIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int id))
                    idSet.Add(id);
            }

            if (idSet.Count == 0)
                mods = [];
            else
                mods = mods.Where(m => idSet.Contains(m.TopicId)).ToList();
        }
        else if (favoritesFirst)
        {
            favoritesFirst = false;
        }

        bool descending = dir.Equals("desc", StringComparison.OrdinalIgnoreCase);

        mods = sort.ToLowerInvariant() switch
        {
            "views" => descending ? mods.OrderByDescending(m => m.Views).ToList() : mods.OrderBy(m => m.Views).ToList(),
            "replies" => descending ? mods.OrderByDescending(m => m.Replies).ToList() : mods.OrderBy(m => m.Replies).ToList(),
            "date" => descending ? mods.OrderByDescending(GetLastActivityDate).ToList() : mods.OrderBy(GetLastActivityDate).ToList(),
            "activity" => descending ? mods.OrderByDescending(GetLastActivityDate).ToList() : mods.OrderBy(GetLastActivityDate).ToList(),
            "created" => descending ? mods.OrderByDescending(GetCreatedDate).ToList() : mods.OrderBy(GetCreatedDate).ToList(),
            "author" => descending ? mods.OrderByDescending(m => m.Author).ToList() : mods.OrderBy(m => m.Author).ToList(),
            _ => descending ? mods.OrderByDescending(m => m.Title).ToList() : mods.OrderBy(m => m.Title).ToList()
        };

        // Enrich with ModRepo + local mod data (await ModRepo so first load is not ahead of background init)
        var enriched = await _matching.EnrichAsync(mods);

        // Lightweight assumed download flag (no HTTP calls - cache only).
        // Runs for all mods regardless of HasDirectDownload: when assumed downloads offer a
        // fresher URL than the ModRepo DirectDownload, the ModRepo URL is suppressed so the
        // list card uses the assumed download flow instead.
        foreach (var e in enriched)
        {
            var cached = _assumed.GetCachedCandidates(e.TopicId);
            if (cached is not { Count: > 0 }) continue;

            e.HasAssumedDownload = true;
            e.AssumedDownloadCount = cached.Count;
            e.AssumedConfidence = cached[0].Confidence;
            e.IsAssumedPatreonLink = cached.Count == 1 && IsPatreonUrl(cached[0].OriginalUrl);
            // Flag the case where the only candidate needs manual user action (no one-click download possible).
            e.AssumedRequiresManualStep = cached.Count == 1 && cached[0].RequiresManualStep;

            ApplyAssumedDownloadOverride(e, cached);
        }

        // Install status filter (applied after enrichment)
        if (!string.IsNullOrWhiteSpace(installStatus))
        {
            enriched = installStatus.ToLowerInvariant() switch
            {
                "installed" => enriched.Where(m => m.IsInstalled).ToList(),
                "not-installed" => enriched.Where(m => !m.IsInstalled).ToList(),
                "update-needed" => enriched.Where(m => m.HasAnyVersionDifference).ToList(),
                _ => enriched
            };
        }

        // Favorites-first sort: favorited topics rank before non-favorited topics.
        if (favoritesFirst)
        {
            var favoriteTopicIds = new HashSet<int>();
            if (!string.IsNullOrWhiteSpace(topicIds))
            {
                foreach (var part in topicIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out int id))
                        favoriteTopicIds.Add(id);
                }
            }

            enriched = enriched
                .OrderByDescending(m => favoriteTopicIds.Contains(m.TopicId) ? 1 : 0)
                .ThenBy(m => enriched.IndexOf(m))
                .ToList();
        }

        // Compute missing-dependency priority set so dep mods sort into the correct tier.
        var depReport = _deps.ComputeReport();
        var depPrioritySet = new HashSet<int>(depReport.PriorityTopicIds.Select(t => t.TopicId));

        // Installed-first + dependency-first combined sort.
        // Priority tiers: uninstalled-dep(3) > enabled-installed(2) > disabled-installed(1) > other(0).
        // Missing required deps are always listed first so they are never buried behind installed mods.
        if (installedFirst || installedCategoryFirst || depPrioritySet.Count > 0)
        {
            var snapshot = enriched.ToList();

            IOrderedEnumerable<EnrichedModSummary> q;
            if (installedFirst)
            {
                // Full four-tier sort: uninstalled-dep > enabled-installed > disabled-installed > rest.
                q = snapshot.OrderByDescending(m =>
                    !m.IsInstalled && depPrioritySet.Contains(m.TopicId) ? 3
                    : m.IsInstalled && (m.IsEnabled || (m.AdditionalLocalMods?.Any(a => a.IsEnabled) == true)) ? 2
                    : m.IsInstalled ? 1
                    : 0);
            }
            else if (depPrioritySet.Count > 0)
            {
                // Dep sort only (installedFirst off): uninstalled-dep rises to top; installed/not-installed are equal.
                q = snapshot.OrderByDescending(m =>
                    !m.IsInstalled && depPrioritySet.Contains(m.TopicId) ? 1 : 0);
            }
            else
            {
                // installedCategoryFirst only, no dep mods: installed(1) > not-installed(0).
                q = snapshot.OrderByDescending(m => m.IsInstalled ? 1 : 0);
            }

            // Secondary: sort installed mods by their category when installedCategoryFirst is on.
            if (installedCategoryFirst)
                q = q.ThenBy(m => m.IsInstalled ? NormalizeCategory(m.Category) : "\uffff");

            enriched = q.ThenBy(m => snapshot.IndexOf(m)).ToList();
        }

        // Updates-first sort: pending updates bubble up, but never above missing required dependencies.
        if (updatesFirst)
        {
            var snapshot = enriched.ToList();
            enriched = snapshot
                .OrderByDescending(m => !m.IsInstalled && depPrioritySet.Contains(m.TopicId) ? 1 : 0)
                .ThenByDescending(m => m.HasAnyVersionDifference ? 1 : 0)
                .ThenBy(m => snapshot.IndexOf(m))
                .ToList();
        }

        int total = enriched.Count;
        int ghostSlots = depReport.UnmatchedDependencies.Count;
        var (paged, totalPages) = PaginateModsForGhostSlots(enriched, total, page, pageSize, ghostSlots);

        IReadOnlyList<UnmatchedDependency> unmatchedForClient = page <= 1
            ? depReport.UnmatchedDependencies
            : Array.Empty<UnmatchedDependency>();

        return Ok(new
        {
            mods = paged,
            total,
            page,
            pageSize,
            totalPages,
            versions = allVersions,
            categories = allCategories,
            depPriorityTopicIds = depReport.PriorityTopicIds,
            unmatchedDependencies = unmatchedForClient
        });
    }

    // Applies one skip/take scheme: page 1 uses (pageSize - ghostSlots) when ghosts exist; page 2+ uses full pageSize with offset adjusted for the shorter first page.
    private static (List<EnrichedModSummary> slice, int totalPages) PaginateModsForGhostSlots(
        List<EnrichedModSummary> rows,
        int total,
        int page,
        int pageSize,
        int ghostSlotCount)
    {
        int firstTake = ghostSlotCount > 0 ? Math.Max(1, pageSize - ghostSlotCount) : pageSize;
        int skip = page <= 1 ? 0 : firstTake + (page - 2) * pageSize;
        int take = page <= 1 ? firstTake : pageSize;
        int totalPages = total <= firstTake ? 1 : 1 + (int)Math.Ceiling((double)(total - firstTake) / pageSize);
        return (rows.Skip(skip).Take(take).ToList(), totalPages);
    }

    // Converts age filter token into a UTC cutoff date.
    private static DateTime? GetCutoff(string age) => age switch
    {
        "1m" => DateTime.UtcNow.AddMonths(-1),
        "2m" => DateTime.UtcNow.AddMonths(-2),
        "6m" => DateTime.UtcNow.AddMonths(-6),
        "1y" => DateTime.UtcNow.AddYears(-1),
        "2y" => DateTime.UtcNow.AddYears(-2),
        "4y" => DateTime.UtcNow.AddYears(-4),
        "8y" => DateTime.UtcNow.AddYears(-8),
        _ => null
    };

    // Returns the best available last-activity date for sorting/filtering.
    private static DateTime GetLastActivityDate(ModSummary mod)
        => ParseDateOrFallback(mod.LastPostDate, mod.ScrapedAt);

    // Returns the best available creation date for sorting/filtering.
    private static DateTime GetCreatedDate(ModSummary mod)
        => ParseDateOrFallback(mod.CreatedDate, mod.ScrapedAt);

    // Parses a date string with fallback to a known scraped timestamp.
    private static DateTime ParseDateOrFallback(string? value, DateTime fallback)
        => !string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    // Normalizes category names so legacy/current values group consistently.
    private static string NormalizeCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ForumConstants.UncategorizedCategory;
        var v = value.Trim();
        if (ForumConstants.IsLibraryCategoryName(v))
            return ForumConstants.LibraryCategory;
        return v;
    }

    // Checks whether a URL points to Patreon so the UI can signal manual-only download flow.
    private static bool IsPatreonUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Contains("patreon.com", StringComparison.OrdinalIgnoreCase);
    }

    // Returns full detail for one topic, including manager data and assumed downloads.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetMod(int id)
    {
        var context = await LoadModContext(id);
        if (context.Detail == null)
            return NotFound(new { error = $"Mod {id} not found" });

        var assumedDownloads = _assumed.GetCachedCandidates(id) ?? [];
        if (context.EnrichedSummary != null)
        {
            context.EnrichedSummary.HasAssumedDownload = assumedDownloads.Count > 0;
            context.EnrichedSummary.AssumedDownloadCount = assumedDownloads.Count;
            ApplyAssumedDownloadOverride(context.EnrichedSummary, assumedDownloads);
        }

        return Ok(new
        {
            detail = context.Detail,
            manager = BuildManagerPayload(id, context.Detail, context.EnrichedSummary),
            assumedDownloads
        });
    }

    // Resolves assumed downloads after initial page load so detail rendering is not blocked.
    [HttpGet("{id:int}/assumed-downloads")]
    public async Task<IActionResult> GetAssumedDownloads(int id)
    {
        var context = await LoadModContext(id);
        if (context.Detail == null)
            return NotFound(new { error = $"Mod {id} not found" });

        var assumedDownloads = new List<AssumedDownloadCandidate>();
        if (context.Detail.Links.Count > 0)
        {
            try
            {
                var linksForAssumed = SpoilerLinkFilter.FilterOutSpoilerLinks(context.Detail.ContentHtml, context.Detail.Links);
                assumedDownloads = await _assumed.ResolveAsync(id, linksForAssumed);
            }
            catch
            {
                assumedDownloads = [];
            }
        }

        if (context.EnrichedSummary != null)
        {
            context.EnrichedSummary.HasAssumedDownload = assumedDownloads.Count > 0;
            context.EnrichedSummary.AssumedDownloadCount = assumedDownloads.Count;
            ApplyAssumedDownloadOverride(context.EnrichedSummary, assumedDownloads);
        }

        return Ok(new
        {
            assumedDownloads,
            manager = BuildManagerPayload(id, context.Detail, context.EnrichedSummary)
        });
    }

    // When assumed downloads offer non-manual candidates with URLs that differ from the ModRepo
    // DirectDownload URL, the assumed downloads are more current (scraped from the live forum post)
    // and should be the primary install mechanism. DirectDownloadUrl is suppressed so the
    // dedicated Download/Update button defers to assumed downloads.
    // When the mod has an update available but no actionable URL remains after suppression,
    // the first assumed candidate's OriginalUrl is promoted to UpdateDownloadUrl so the
    // Update button stays functional. OriginalUrl is used (not ResolvedDirectUrl) because
    // CDN URLs are session-scoped; the download manager re-resolves on demand.
    // UpdateDownloadUrl supplied by the version checker is authoritative and is never cleared.
    private static void ApplyAssumedDownloadOverride(EnrichedModSummary summary, List<AssumedDownloadCandidate> candidates)
    {
        var nonManual = candidates.Where(c => !c.RequiresManualStep).ToList();
        if (nonManual.Count == 0) return;

        // Suppress stale ModRepo DirectDownload when assumed downloads have a different URL.
        if (!string.IsNullOrEmpty(summary.DirectDownloadUrl))
        {
            var directNorm = NormalizeUrlForComparison(summary.DirectDownloadUrl);
            bool coveredByAssumed = nonManual.Any(c =>
                NormalizeUrlForComparison(c.OriginalUrl) == directNorm ||
                NormalizeUrlForComparison(c.ResolvedDirectUrl) == directNorm);

            if (!coveredByAssumed)
                summary.DirectDownloadUrl = null;
        }

        // Recompute after suppression.
        summary.HasDirectDownload = !string.IsNullOrEmpty(summary.DirectDownloadUrl)
            || !string.IsNullOrEmpty(summary.UpdateDownloadUrl);

        // When an update is flagged but there is no longer any actionable download URL,
        // promote the first assumed candidate so the Update button stays functional rather
        // than showing the "manual download required" disabled state.
        if (summary.UpdateAvailable && !summary.HasDirectDownload)
        {
            var best = nonManual.FirstOrDefault();
            if (best != null)
            {
                summary.UpdateDownloadUrl = best.OriginalUrl;
                summary.HasDirectDownload = true;
            }
        }
    }

    private static string NormalizeUrlForComparison(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var uri = new Uri(url.Trim());
            return (uri.Scheme + "://" + uri.Authority + uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        }
        catch { return url.Trim().TrimEnd('/').ToLowerInvariant(); }
    }

    // Loads detail and enrichment once so multiple endpoints share the same fast context logic.
    private async Task<(ModDetail? Detail, EnrichedModSummary? EnrichedSummary)> LoadModContext(int id)
    {
        var index = await _store.LoadIndex();
        var summary = index.FirstOrDefault(m => m.TopicId == id);
        var detail = await _store.LoadDetail(id);
        if (detail == null)
        {
            if (summary == null)
                return (null, null);

            detail = BuildPlaceholderDetail(summary);
        }
        else if (summary != null)
        {
            detail.Category = summary.Category;
        }

        var knownTopicIds = index.Select(m => m.TopicId).ToHashSet();
        ForumLocalModLinkRewriter.Apply(detail, knownTopicIds);

        // Strip smiley <img> tags from stored HTML (replaces with alt text)
        if (!string.IsNullOrEmpty(detail.ContentHtml))
        {
            detail.ContentHtml = Regex.Replace(
                detail.ContentHtml,
                @"<img\s+[^>]*?src=""[^""]*?/Smileys/[^""]*""[^>]*?>",
                match =>
                {
                    var altMatch = Regex.Match(match.Value, @"alt=""([^""]*?)""", RegexOptions.IgnoreCase);
                    return altMatch.Success ? altMatch.Groups[1].Value : "";
                },
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            detail.ContentHtml = RewriteExternalImageSourcesToProxy(detail.ContentHtml);
        }

        EnrichedModSummary? enrichedSummary = null;
        if (summary != null)
        {
            var enrichedList = await _matching.EnrichAsync([summary]);
            enrichedSummary = enrichedList.FirstOrDefault();
        }

        return (detail, enrichedSummary);
    }

    // Shapes manager payload fields used by mod detail page responses.
    private object? BuildManagerPayload(int topicId, ModDetail detail, EnrichedModSummary? enrichedSummary)
    {
        if (enrichedSummary == null)
            return null;

        return new
        {
            enrichedSummary.ModRepoName,
            enrichedSummary.DirectDownloadUrl,
            enrichedSummary.DownloadPageUrl,
            enrichedSummary.IsInstalled,
            enrichedSummary.IsEnabled,
            enrichedSummary.LocalModId,
            enrichedSummary.LocalModName,
            enrichedSummary.LocalVersion,
            enrichedSummary.LocalGameVersion,
            enrichedSummary.OnlineVersion,
            enrichedSummary.UpdateAvailable,
            enrichedSummary.HasDirectDownload,
            enrichedSummary.UpdateDownloadUrl,
            enrichedSummary.ModRepoVersion,
            enrichedSummary.HasAssumedDownload,
            enrichedSummary.AssumedDownloadCount,
            enrichedSummary.AdditionalLocalMods,
            TopicArchiveEntries = _matching.GetTopicArchiveEntries(topicId, detail)
        };
    }

    // Rewrites non-forum image tags to local image proxy URLs so browser loads are stable.
    private static string RewriteExternalImageSourcesToProxy(string html)
    {
        return Regex.Replace(
            html,
            @"<img(?<before>[^>]*?)\s+src=""(?<src>[^""]+)""(?<after>[^>]*)>",
            match =>
            {
                var src = match.Groups["src"].Value;
                if (string.IsNullOrWhiteSpace(src))
                    return match.Value;
                if (!Uri.TryCreate(src, UriKind.Absolute, out var srcUri))
                    return match.Value;
                if (srcUri.Scheme is not ("http" or "https"))
                    return match.Value;
                if (ForumConstants.IsForumHosted(src))
                    return match.Value;

                var proxied = "/api/images/external?url=" + Uri.EscapeDataString(src);
                return $@"<img{match.Groups["before"].Value} src=""{proxied}""{match.Groups["after"].Value}>";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    // Builds a placeholder detail object when only index data exists for a topic.
    private static ModDetail BuildPlaceholderDetail(ModSummary s)
    {
        string forumUrl = ForumConstants.TopicUrl(s.TopicId);
        return new ModDetail
        {
            TopicId = s.TopicId,
            Title = s.Title,
            Category = s.Category,
            GameVersion = s.GameVersion,
            Author = s.Author,
            PostDate = s.CreatedDate,
            ContentHtml =
                "<p class=\"text-amber-200/90 border border-amber-500/40 bg-amber-950/40 rounded px-3 py-2 mb-4\">" +
                "Full post text has not been scraped to disk yet (topic is listed in the index). " +
                "Run a scrape that includes this topic, or open the link below." +
                "</p>",
            Images = [],
            Links =
            [
                new LinkRef { Url = forumUrl, Text = "Original forum thread", IsExternal = true }
            ],
            ScrapedAt = s.ScrapedAt,
            IsPlaceholderDetail = true
        };
    }
}

