using Microsoft.Playwright;
using QBModsBrowser.Scraper.Models;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Scrapes forum board pages to build the mod listing from topic rows.
public class BoardScraper
{
    private const int TopicsPerPage = 20;
    private readonly ILogger _log;
    private readonly int _delayMs;

    public BoardScraper(ILogger logger, int delayBetweenPagesMs = 1500)
    {
        _log = logger.ForContext<BoardScraper>();
        _delayMs = delayBetweenPagesMs;
    }

    public async Task<List<ModSummary>> ScrapeAllPages(
        IPage page,
        int? maxPages,
        CancellationToken ct,
        Func<IReadOnlyList<ModSummary>, bool>? shouldContinueAfterPage = null,
        bool sortByLastPostDesc = false,
        string? boardBaseUrl = null,
        Func<string, bool>? topicTitleFilter = null)
    {
        string baseUrl = boardBaseUrl ?? ForumConstants.BoardUrl;
        var allMods = new List<ModSummary>();
        int pageIndex = 0;
        HashSet<int>? previousTopicIds = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int offset = pageIndex * TopicsPerPage;
            string sortSuffix = sortByLastPostDesc ? ";sort=last_post;desc" : "";
            string url = $"{baseUrl}{offset}{sortSuffix}";
            _log.Information("Scraping board page {Page} (offset {Offset}): {Url}", pageIndex + 1, offset, url);

            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await CloudflareHelper.WaitForCloudflare(page, _log, cancellationToken: ct);

            bool skipStickyTopics = !ForumConstants.IsLibraryBoardBase(baseUrl);
            var mods = await ExtractTopicsFromPage(page, topicTitleFilter, skipStickyTopics);
            _log.Information("Found {Count} topics on page {Page}", mods.Count, pageIndex + 1);

            if (mods.Count == 0)
                break;

            // SMF will keep returning the last page if the offset is beyond the end.
            // Detect that by comparing topic IDs with the previous page.
            var topicIds = mods.Select(m => m.TopicId).ToHashSet();
            if (previousTopicIds != null && topicIds.SetEquals(previousTopicIds))
            {
                _log.Information("Detected repeated last page at page {Page} (offset {Offset}); stopping.", pageIndex + 1, offset);
                break;
            }
            previousTopicIds = topicIds;

            allMods.AddRange(mods);

            if (shouldContinueAfterPage != null && !shouldContinueAfterPage(mods))
            {
                _log.Information("Early-stop triggered after page {Page}: no matching topics for current scope.", pageIndex + 1);
                break;
            }

            pageIndex++;

            if (maxPages.HasValue && pageIndex >= maxPages.Value)
            {
                _log.Information("Reached max pages limit ({Max})", maxPages.Value);
                break;
            }

            // Check for next page
            bool hasNextPage = await page.Locator("a.navPages").CountAsync() > 0;
            if (!hasNextPage)
            {
                _log.Information("No more pages after page {Page}", pageIndex);
                break;
            }

            await Task.Delay(_delayMs, ct);
        }

        _log.Information("Board scrape complete: {Total} topics across {Pages} pages", allMods.Count, pageIndex);
        return allMods;
    }

    private async Task<List<ModSummary>> ExtractTopicsFromPage(
        IPage page,
        Func<string, bool>? topicTitleFilter,
        bool skipStickyTopics)
    {
        var mods = new List<ModSummary>();

        // Each topic row contains a span[id^='msg_'] with the topic link
        var topicSpans = page.Locator("span[id^='msg_']");
        int count = await topicSpans.CountAsync();

        for (int i = 0; i < count; i++)
        {
            try
            {
                var span = topicSpans.Nth(i);
                var link = span.Locator("a").First;
                if (await link.CountAsync() == 0) continue;

                string title = (await link.InnerTextAsync()).Trim();
                if (topicTitleFilter != null && !topicTitleFilter(title))
                    continue;

                string? href = await link.GetAttributeAsync("href");
                if (!ForumConstants.TryExtractTopicId(href, out int topicId))
                    continue;

                // Sticky threads are still listed on the main mods board; on the library board many resources are sticky.
                if (skipStickyTopics)
                {
                    var parentTd = span.Locator("xpath=ancestor::td");
                    var stickyImg = parentTd.Locator("img[src*='show_sticky.gif']");
                    if (await stickyImg.CountAsync() > 0)
                        continue;
                }

                var versionMatch = ForumConstants.GameVersionRegex().Match(title);
                string? gameVersion = versionMatch.Success ? versionMatch.Groups[1].Value : null;

                // Navigate to parent row to get other cells
                var parentRow = span.Locator("xpath=ancestor::tr");

                string author = "";
                var starterCell = parentRow.Locator("td.starter a");
                if (await starterCell.CountAsync() > 0)
                    author = (await starterCell.First.InnerTextAsync()).Trim();

                int replies = 0, views = 0;
                var repliesCell = parentRow.Locator("td.replies");
                if (await repliesCell.CountAsync() > 0)
                    int.TryParse((await repliesCell.InnerTextAsync()).Trim().Replace(",", ""), out replies);
                var viewsCell = parentRow.Locator("td.views");
                if (await viewsCell.CountAsync() > 0)
                    int.TryParse((await viewsCell.InnerTextAsync()).Trim().Replace(",", ""), out views);

                string? lastPostDate = null;
                string? lastPostBy = null;
                var lastpostCell = parentRow.Locator("td.lastpost");
                if (await lastpostCell.CountAsync() > 0)
                {
                    var smalltext = lastpostCell.Locator(".smalltext");
                    if (await smalltext.CountAsync() > 0)
                    {
                        string lpText = await smalltext.InnerTextAsync();
                        var parts = lpText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1) lastPostDate = parts[0];
                        if (parts.Length >= 2) lastPostBy = parts[1].Replace("by ", "").Trim();
                    }
                }

                mods.Add(new ModSummary
                {
                    TopicId = topicId,
                    Title = title,
                    GameVersion = gameVersion,
                    Author = author,
                    Replies = replies,
                    Views = views,
                    LastPostDate = lastPostDate,
                    LastPostBy = lastPostBy,
                    TopicUrl = ForumConstants.TopicUrl(topicId),
                    ScrapedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to parse topic span {Index}", i);
            }
        }

        return mods;
    }
}

