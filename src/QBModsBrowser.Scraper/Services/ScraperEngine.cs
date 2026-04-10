using Microsoft.Playwright;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Runs the full Playwright-based scrape pipeline: board listing, topic detail, and image download.
public class ScraperEngine : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly JsonDataStore _store;
    private readonly string _dataPath;
    private readonly int _pageDelayMs;
    private readonly int _topicDelayMs;

    private IPlaywright? _playwright;
    private IBrowserContext? _context;

    public ScrapeJob CurrentJob { get; } = new();

    public ScraperEngine(ILogger logger, JsonDataStore store, string dataPath,
        int pageDelayMs = 1500, int topicDelayMs = 1500)
    {
        _log = logger.ForContext<ScraperEngine>();
        _store = store;
        _dataPath = dataPath;
        _pageDelayMs = pageDelayMs;
        _topicDelayMs = topicDelayMs;
    }

    // Runs a scrape job end-to-end, updating CurrentJob for the control panel.
    // onTopicSaved, if provided, is invoked concurrently after each topic's detail is saved
    // so callers can run resolution/enrichment without blocking the scrape loop.
    public async Task<ScrapeResult> Run(ScrapeScope scope, CancellationToken ct,
        Func<ModDetail, Task>? onTopicSaved = null)
    {
        var startTime = DateTime.UtcNow;
        CurrentJob.State = ScrapeState.Scraping;
        CurrentJob.Scope = scope;
        CurrentJob.StartedAt = startTime;
        CurrentJob.ErrorMessage = null;
        CurrentJob.CurrentPhase = "Launching browser";

        try
        {
            _log.Information("Starting scrape job {JobId} with scope {ScopeType}", CurrentJob.Id, scope.Type);

            _playwright = await Playwright.CreateAsync();

            // Use a persistent browser profile to survive Cloudflare challenges.
            // The cf_clearance cookie persists between runs.
            string profileDir = Path.Combine(_dataPath, "browser-profile");
            Directory.CreateDirectory(profileDir);

            _context = await _playwright.Chromium.LaunchPersistentContextAsync(profileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    Args = [
                        "--disable-blink-features=AutomationControlled",
                        "--no-first-run",
                        "--no-default-browser-check"
                    ]
                });

            await _context.AddInitScriptAsync(
                "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");

            // Hard boundary: scraper must never request non-forum hosts,
            // except challenges.cloudflare.com which must be reachable for the
            // Cloudflare bot-check to complete on a fresh browser profile.
            await _context.RouteAsync("**/*", async route =>
            {
                var requestUrl = route.Request.Url;
                if (requestUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    requestUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                    requestUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    await route.ContinueAsync();
                    return;
                }

                if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) &&
                    uri.Host.Equals("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    await route.ContinueAsync();
                    return;
                }

                if (!ForumConstants.IsForumHosted(requestUrl))
                {
                    _log.Debug("Blocked external request: {Url}", requestUrl);
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();

            var boardScraper = new BoardScraper(_log, _pageDelayMs);
            var topicScraper = new TopicScraper(_log, _topicDelayMs);
            var modIndexCategoryScraper = new ModIndexCategoryScraper(_log);
            var htmlProcessor = new HtmlProcessor(_log);
            var existingIndex = await _store.LoadIndex();
            var indexMap = existingIndex.ToDictionary(m => m.TopicId);
            // Snapshot meaningful fields before any in-memory mutations (category enrichment etc.)
            var preScrapeSnapshot = existingIndex.ToDictionary(m => m.TopicId, SnapshotMeaningfulFields);
            var meaningfullyChangedIds = new List<int>();
            CurrentJob.CurrentPhase = "Mod index (topic 177)";
            var modIndex = await modIndexCategoryScraper.Scrape(ct, page);
            foreach (var existing in indexMap.Values)
            {
                ApplyCategoryFromModIndex(existing, modIndex);
            }

            if (modIndex.UnknownLegacyCategories.Count > 0)
            {
                CurrentJob.ErrorMessage =
                    $"Unmapped legacy mod-index categories found. Please update legacy-to-current category map at '{ModIndexCategoryScraper.LegacyCategoryMapJsonPath}'. " +
                    $"Unknown: {string.Join(", ", modIndex.UnknownLegacyCategories.Distinct(StringComparer.OrdinalIgnoreCase))}";
                _log.Warning(CurrentJob.ErrorMessage);
            }

            List<ModSummary> modSummaries;
            var mainTopicIds = new HashSet<int>();
            var lesserTopicIds = new HashSet<int>();
            var libraryTopicIds = new HashSet<int>();

            if (scope.Type == ScopeType.Topics && scope.TopicIds?.Count > 0)
            {
                modSummaries = scope.TopicIds.Select(id => new ModSummary
                {
                    TopicId = id,
                    TopicUrl = ForumConstants.TopicUrl(id)
                }).ToList();
                _log.Information("Scraping {Count} specific topics", modSummaries.Count);
            }
            else if (scope.Type == ScopeType.LibrariesOnly)
            {
                _log.Information("Scraping library board (board 9) only");
                CurrentJob.CurrentPhase = "Library board (board 9)";
                modSummaries = await boardScraper.ScrapeAllPages(
                    page,
                    maxPages: null,
                    ct,
                    shouldContinueAfterPage: null,
                    sortByLastPostDesc: true,
                    boardBaseUrl: ForumConstants.LibraryBoardUrl,
                    topicTitleFilter: ForumConstants.IsLibraryThreadTitle);
                foreach (var m in modSummaries)
                {
                    libraryTopicIds.Add(m.TopicId);
                    m.SourceBoard = 9;
                }
            }
            else
            {
                int? maxPages = scope.Type == ScopeType.Pages ? scope.MaxPages : null;
                bool isNewData = scope.Type == ScopeType.NewData;

                var mainList = new List<ModSummary>();
                var lesserList = new List<ModSummary>();
                var libraryList = new List<ModSummary>();

                // Scrape main mods board (board 8) when enabled.
                if (scope.Boards.HasFlag(ScrapeBoards.Main))
                {
                    Func<IReadOnlyList<ModSummary>, bool>? shouldContinueMain = isNewData
                        ? pageMods => pageMods.Any(m => IsNewOrLastPostChanged(m, indexMap))
                        : null;

                    CurrentJob.CurrentPhase = "Mods board (8)";
                    mainList = await boardScraper.ScrapeAllPages(
                        page,
                        maxPages,
                        ct,
                        shouldContinueMain,
                        sortByLastPostDesc: isNewData);
                    foreach (var m in mainList)
                    {
                        mainTopicIds.Add(m.TopicId);
                        m.SourceBoard = 8;
                        m.IsWip = ForumConstants.IsWipTitle(m.Title);
                    }
                    await Task.Delay(_pageDelayMs, ct);
                }

                // Scrape lesser mods board (board 3) when enabled — capped at LesserBoardMaxPages.
                if (scope.Boards.HasFlag(ScrapeBoards.Lesser))
                {
                    int lesserMax = Math.Min(
                        scope.MaxPages ?? ForumConstants.LesserBoardMaxPages,
                        ForumConstants.LesserBoardMaxPages);

                    Func<IReadOnlyList<ModSummary>, bool>? shouldContinueLesser = isNewData
                        ? pageMods => pageMods.Any(m => IsNewOrLastPostChanged(m, indexMap))
                        : null;

                    CurrentJob.CurrentPhase = "Lesser mods board (3)";
                    _log.Information("Scraping lesser mods board (board 3), max {Max} pages", lesserMax);
                    lesserList = await boardScraper.ScrapeAllPages(
                        page,
                        lesserMax,
                        ct,
                        shouldContinueLesser,
                        sortByLastPostDesc: isNewData,
                        boardBaseUrl: ForumConstants.LesserBoardUrl,
                        topicTitleFilter: ForumConstants.IsLesserBoardTopicTitle);
                    foreach (var m in lesserList)
                    {
                        lesserTopicIds.Add(m.TopicId);
                        m.SourceBoard = 3;
                        m.IsWip = ForumConstants.IsWipTitle(m.Title);
                    }
                    await Task.Delay(_pageDelayMs, ct);
                }

                // Scrape libraries board (board 9) when enabled.
                if (scope.Boards.HasFlag(ScrapeBoards.Libraries))
                {
                    Func<IReadOnlyList<ModSummary>, bool>? shouldContinueLibrary = null;
                    if (isNewData)
                    {
                        shouldContinueLibrary = pageMods =>
                        {
                            var matching = pageMods.Where(m => ForumConstants.IsLibraryThreadTitle(m.Title)).ToList();
                            if (matching.Count == 0)
                                return true;
                            return matching.Any(m => IsNewOrLastPostChanged(m, indexMap));
                        };
                    }

                    _log.Information("Scraping library board (board 9)");
                    CurrentJob.CurrentPhase = "Library board (9)";
                    libraryList = await boardScraper.ScrapeAllPages(
                        page,
                        maxPages,
                        ct,
                        shouldContinueLibrary,
                        sortByLastPostDesc: true,
                        boardBaseUrl: ForumConstants.LibraryBoardUrl,
                        topicTitleFilter: ForumConstants.IsLibraryThreadTitle);
                    foreach (var m in libraryList)
                    {
                        libraryTopicIds.Add(m.TopicId);
                        m.SourceBoard = 9;
                    }
                }

                modSummaries = MergeDedupeBoards(mainList, lesserList, libraryList);

                if (isNewData)
                    modSummaries = ApplyIncrementalFilter(modSummaries, indexMap, _log);
            }

            CurrentJob.TotalTopics = modSummaries.Count;
            _log.Information("Found {Count} topics to scrape", modSummaries.Count);
            CurrentJob.CurrentPhase = null;

            // Tracks concurrent per-topic callbacks (e.g. assumed-download resolution) started during the loop.
            var pendingCallbacks = new List<Task>();

            foreach (var summary in modSummaries)
            {
                ct.ThrowIfCancellationRequested();
                ApplyCategoryFromModIndex(summary, modIndex);

                // Library-only topics get the library category when they didn't come from the main board.
                if (libraryTopicIds.Contains(summary.TopicId) && !mainTopicIds.Contains(summary.TopicId))
                    summary.Category = ForumConstants.LibraryCategory;

                // Any uncategorized topic (board-3 is never indexed; main-board mods may have been removed
                // from the mod index) gets a title-keyword guess as a fallback.
                if (!summary.InModIndex)
                    summary.Category = ForumConstants.GuessCategoryFromTitle(summary.Title);

                CurrentJob.CurrentItem = summary.Title.Length > 0 ? summary.Title : $"Topic {summary.TopicId}";
                _log.Information("Scraping topic {TopicId}: {Title}", summary.TopicId, CurrentJob.CurrentItem);

                var detail = await topicScraper.ScrapeTopic(page, summary.TopicId, ct);

                if (detail != null)
                {
                    // Board-3 topics with no off-site http(s) links (aside from forum/Nexus/YouTube) are not useful — skip them.
                    if (summary.SourceBoard == 3 && !ForumConstants.HasFileHostingLinks(detail.Links))
                    {
                        _log.Information(
                            "Board-3 topic {TopicId} has no qualifying external links; skipping.",
                            summary.TopicId);
                        CurrentJob.ProcessedTopics++;
                        continue;
                    }

                    if (string.IsNullOrEmpty(summary.Title))
                    {
                        summary.Title = detail.Title;
                        summary.GameVersion = detail.GameVersion;
                        summary.Author = detail.Author;
                    }
                    summary.CreatedDate = detail.PostDate;
                    summary.ScrapedAt = DateTime.UtcNow;

                    detail.ContentHtml = htmlProcessor.ProcessHtml(detail.ContentHtml, detail.TopicId, detail.Images);
                    await _store.SaveDetail(detail);

                    // Fire per-topic callback concurrently — scrape loop does not wait for it.
                    if (onTopicSaved != null)
                        pendingCallbacks.Add(onTopicSaved(detail));

                    summary.ThumbnailPath = _store.PickThumbnail(detail.TopicId, detail.Images);
                }
                else
                {
                    CurrentJob.Errors++;
                }

                indexMap[summary.TopicId] = summary;
                if (!preScrapeSnapshot.TryGetValue(summary.TopicId, out var prevSnap) || HasMeaningfulChanges(summary, prevSnap))
                    meaningfullyChangedIds.Add(summary.TopicId);
                CurrentJob.ProcessedTopics++;
            }

            // Wait for all per-topic callbacks (e.g. resolution) to finish before writing the final index.
            if (pendingCallbacks.Count > 0)
                await Task.WhenAll(pendingCallbacks);

            var finalIndex = indexMap.Values.OrderByDescending(m => m.ScrapedAt).ToList();
            await _store.SaveIndex(finalIndex);

            LogMeaningfulChanges(meaningfullyChangedIds, modSummaries.Count);

            CurrentJob.State = ScrapeState.Completed;
            CurrentJob.FinishedAt = DateTime.UtcNow;

            var result = BuildResult(true, startTime);
            _log.Information("Scrape completed: {Mods} mods, {Images} images, {Errors} errors in {Duration}",
                result.ModsScraped, result.ImagesDownloaded, result.Errors, result.Duration);
            return result;
        }
        catch (OperationCanceledException)
        {
            CurrentJob.State = ScrapeState.Cancelled;
            CurrentJob.FinishedAt = DateTime.UtcNow;
            _log.Warning("Scrape job cancelled");
            return BuildResult(false, startTime, "Cancelled");
        }
        catch (Exception ex)
        {
            CurrentJob.State = ScrapeState.Failed;
            CurrentJob.FinishedAt = DateTime.UtcNow;
            CurrentJob.ErrorMessage = ex.Message;
            _log.Error(ex, "Scrape job failed");
            return BuildResult(false, startTime, ex.Message, extraErrors: 1);
        }
        finally
        {
            CurrentJob.CurrentPhase = null;
            await DisposeAsync();
        }
    }

    private ScrapeResult BuildResult(bool success, DateTime startTime,
        string? errorMessage = null, int extraErrors = 0) => new()
    {
        Success = success,
        ModsScraped = CurrentJob.ProcessedTopics,
        ImagesDownloaded = CurrentJob.DownloadedImages,
        Errors = CurrentJob.Errors + extraErrors,
        Duration = (CurrentJob.FinishedAt ?? DateTime.UtcNow) - startTime,
        ErrorMessage = errorMessage
    };

    // Captures fields used for post-scrape change detection; excludes ScrapedAt, Views, Replies, LastPostDate, and LastPostBy.
    private static MeaningfulSummarySnapshot SnapshotMeaningfulFields(ModSummary s) => new(
        s.Title, s.Category, s.InModIndex, s.IsArchivedModIndex,
        s.GameVersion, s.Author, s.CreatedDate, s.ThumbnailPath, s.IsWip, s.SourceBoard);

    // Returns true when any meaningful field differs from the pre-scrape snapshot.
    private static bool HasMeaningfulChanges(ModSummary s, MeaningfulSummarySnapshot old) =>
        s.Title != old.Title ||
        s.Category != old.Category ||
        s.InModIndex != old.InModIndex ||
        s.IsArchivedModIndex != old.IsArchivedModIndex ||
        s.GameVersion != old.GameVersion ||
        s.Author != old.Author ||
        s.CreatedDate != old.CreatedDate ||
        s.ThumbnailPath != old.ThumbnailPath ||
        s.IsWip != old.IsWip ||
        s.SourceBoard != old.SourceBoard;

    // Logs the count and sorted IDs of topics with meaningful field changes after a scrape.
    private void LogMeaningfulChanges(List<int> changedIds, int totalScraped)
    {
        changedIds.Sort();
        if (changedIds.Count == 0)
            _log.Information("Meaningful changes: none among {Total} scraped topic(s)", totalScraped);
        else
            _log.Information("Meaningful changes in {Count}/{Total} scraped topic(s) — IDs: {Ids}",
                changedIds.Count, totalScraped, changedIds);
    }

    private static bool IsNewOrLastPostChanged(ModSummary summary, IReadOnlyDictionary<int, ModSummary> indexMap)
    {
        if (!indexMap.TryGetValue(summary.TopicId, out var existing))
            return true;

        return !string.Equals(existing.LastPostDate, summary.LastPostDate, StringComparison.Ordinal);
    }

    // Merges main, lesser, and library board lists in priority order, deduplicating by topic ID.
    private static List<ModSummary> MergeDedupeBoards(
        List<ModSummary> main,
        List<ModSummary> lesser,
        List<ModSummary> library)
    {
        var seen = new HashSet<int>();
        var result = new List<ModSummary>(main.Count + lesser.Count + library.Count);
        foreach (var m in main)
            if (seen.Add(m.TopicId)) result.Add(m);
        foreach (var m in lesser)
            if (seen.Add(m.TopicId)) result.Add(m);
        foreach (var m in library)
            if (seen.Add(m.TopicId)) result.Add(m);
        return result;
    }

    private static List<ModSummary> ApplyIncrementalFilter(
        List<ModSummary> modSummaries,
        IReadOnlyDictionary<int, ModSummary> indexMap,
        ILogger log)
    {
        var filtered = new List<ModSummary>(modSummaries.Count);
        foreach (var summary in modSummaries)
        {
            if (!indexMap.TryGetValue(summary.TopicId, out var existing))
            {
                log.Information("Incremental: new topic {TopicId}", summary.TopicId);
                filtered.Add(summary);
                continue;
            }

            if (!string.Equals(existing.LastPostDate, summary.LastPostDate, StringComparison.Ordinal))
            {
                log.Information(
                    "Incremental: topic {TopicId} changed by last post ({OldLastPost} -> {NewLastPost})",
                    summary.TopicId,
                    existing.LastPostDate,
                    summary.LastPostDate);
                filtered.Add(summary);
                continue;
            }

            log.Debug("Incremental: skipping unchanged topic {TopicId}", summary.TopicId);
        }

        return filtered;
    }

    private static void ApplyCategoryFromModIndex(ModSummary summary, ModIndexCategoriesResult categories)
    {
        if (categories.MainTopicCategoryMap.TryGetValue(summary.TopicId, out var mainCategory))
        {
            summary.Category = mainCategory;
            summary.InModIndex = true;
            summary.IsArchivedModIndex = false;
            return;
        }

        if (categories.ArchivedTopicCategoryMap.TryGetValue(summary.TopicId, out var archivedCategory))
        {
            summary.Category = archivedCategory;
            summary.InModIndex = true;
            summary.IsArchivedModIndex = true;
            return;
        }

        summary.Category = ForumConstants.UncategorizedCategory;
        summary.InModIndex = false;
        summary.IsArchivedModIndex = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        GC.SuppressFinalize(this);
    }

    // Value snapshot of ModSummary fields that matter for change detection
    // (excludes ScrapedAt, Views, Replies, LastPostDate, and LastPostBy).
    private readonly record struct MeaningfulSummarySnapshot(
        string Title,
        string Category,
        bool InModIndex,
        bool IsArchivedModIndex,
        string? GameVersion,
        string Author,
        string? CreatedDate,
        string? ThumbnailPath,
        bool IsWip,
        int? SourceBoard);
}



