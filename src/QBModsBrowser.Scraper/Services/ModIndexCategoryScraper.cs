using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Scrapes the forum mod index pages to assign curated categories and game versions to topics.
public class ModIndexCategoryScraper
{
    private readonly ILogger _log;
    private readonly int _delayMs;
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> LegacyCategoryMap =
        new(LoadLegacyCategoryMap, isThreadSafe: true);

    // Resolves the on-disk JSON file used for legacy-to-current category mapping.
    public static string LegacyCategoryMapJsonPath =>
        Path.Combine(AppContext.BaseDirectory, "legacy-category-map.json");

    // Loads the legacy-to-current category mapping from JSON, falling back to empty if missing/invalid.
    private static IReadOnlyDictionary<string, string> LoadLegacyCategoryMap()
    {
        try
        {
            if (!File.Exists(LegacyCategoryMapJsonPath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(LegacyCategoryMapJsonPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed == null || parsed.Count == 0)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public ModIndexCategoryScraper(ILogger logger, int delayMs = 600)
    {
        _log = logger.ForContext<ModIndexCategoryScraper>();
        _delayMs = delayMs;
    }

    public async Task<ModIndexCategoriesResult> Scrape(CancellationToken ct, IPage page)
    {
        var result = new ModIndexCategoriesResult();
        const string url = "https://fractalsoftworks.com/forum/index.php?topic=177.0";

        try
        {
            ct.ThrowIfCancellationRequested();
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await CloudflareHelper.WaitForCloudflare(page, _log, cancellationToken: ct);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(_delayMs, ct);

            int topicLinks = await CountTopicLinks(page);
            _log.Information("Mod index initial topic-link count: {Count}", topicLinks);
            if (topicLinks < 20)
            {
                // SMF can sometimes show reduced content; the ';all' variant is often fuller.
                string allUrl = $"{url};all";
                await page.GotoAsync(allUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                await CloudflareHelper.WaitForCloudflare(page, _log, cancellationToken: ct);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(_delayMs, ct);
                topicLinks = await CountTopicLinks(page);
                _log.Information("Mod index ';all' topic-link count: {Count}", topicLinks);
            }

            var allPosts = page.Locator("#forumposts .post .inner");
            int postCount = await allPosts.CountAsync();
            if (postCount == 0)
            {
                _log.Warning("Mod index: no post bodies found");
                return result;
            }

            var mainMap = await ExtractTopicCategoriesFromPost(allPosts.Nth(0), ct);
            result.MainTopicCategoryMap = mainMap;
            result.MainCategories = mainMap.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.ArchivedTopicCategoryMap = new Dictionary<int, string>();

            for (int i = 1; i < postCount; i++)
            {
                var archivedMap = await ExtractTopicCategoriesFromPost(allPosts.Nth(i), ct);
                foreach (var kv in archivedMap)
                {
                    // Skip topics already present in main post categories.
                    if (mainMap.ContainsKey(kv.Key))
                        continue;

                    var normalized = NormalizeCategory(kv.Value);
                    if (!result.MainCategories.Contains(normalized))
                    {
                        if (LegacyCategoryMap.Value.TryGetValue(normalized, out var mapped) &&
                            result.MainCategories.Contains(mapped))
                        {
                            normalized = mapped;
                        }
                        else
                        {
                            result.UnknownLegacyCategories.Add(normalized);
                            normalized = ForumConstants.UncategorizedCategory;
                        }
                    }

                    result.ArchivedTopicCategoryMap[kv.Key] = normalized;
                }
            }

            var categoryNames = result.MainTopicCategoryMap.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sampleMappings = result.MainTopicCategoryMap.Take(10)
                .Select(kv => $"{kv.Key}:{kv.Value}")
                .ToArray();
            _log.Information("Mod index distinct categories: {Categories}", string.Join(", ", categoryNames));
            _log.Information("Mod index sample topic-category mappings: {Mappings}", string.Join("; ", sampleMappings));
            _log.Information("Parsed {Count} main-post topic categories from mod index", result.MainTopicCategoryMap.Count);
            _log.Information("Parsed {Count} archived-only topic categories from mod index", result.ArchivedTopicCategoryMap.Count);
            if (result.UnknownLegacyCategories.Count > 0)
            {
                _log.Warning("Unmapped legacy categories detected (map: {Path}): {Categories}",
                    LegacyCategoryMapJsonPath,
                    string.Join(", ", result.UnknownLegacyCategories.Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Failed to scrape mod index categories; using uncategorized fallback");
            return result;
        }
    }

    private static string NormalizeCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ForumConstants.UncategorizedCategory;

        var cleaned = SpaceRegex.Replace(raw.Trim(), " ");
        return string.IsNullOrWhiteSpace(cleaned) ? ForumConstants.UncategorizedCategory : cleaned;
    }

    private async Task<Dictionary<int, string>> ExtractTopicCategoriesFromPost(ILocator postRoot, CancellationToken ct)
    {
        var parsed = new Dictionary<int, string>();
        var categoryNodes = postRoot.Locator("xpath=.//table[contains(concat(' ', normalize-space(@class), ' '), ' bbc_table ')]/tbody/tr/td/strong");
        int categoryCount = await categoryNodes.CountAsync();

        for (int i = 0; i < categoryCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var categoryNode = categoryNodes.Nth(i);
            var categoryRaw = (await categoryNode.InnerTextAsync()).Trim();
            var category = NormalizeCategory(WebUtility.HtmlDecode(categoryRaw).TrimEnd(':').Trim());
            if (string.IsNullOrWhiteSpace(category))
                continue;

            var nextList = categoryNode.Locator(
                "xpath=following-sibling::ul[contains(concat(' ', normalize-space(@class), ' '), ' bbc_list ')][1]");
            if (await nextList.CountAsync() == 0)
                continue;

            var topicLinks = nextList.First.Locator("a[href*='topic='], a[href*='topic,'], a[href*='topic/']");
            int linkCount = await topicLinks.CountAsync();
            for (int linkIdx = 0; linkIdx < linkCount; linkIdx++)
            {
                string? href = await topicLinks.Nth(linkIdx).GetAttributeAsync("href");
                if (!ForumConstants.TryExtractTopicId(href, out int topicId))
                    continue;

                parsed.TryAdd(topicId, category);
            }
        }
        return parsed;
    }

    private static async Task<int> CountTopicLinks(IPage page)
    {
        var eqCount = await page.Locator("a[href*='topic=']").CountAsync();
        var commaCount = await page.Locator("a[href*='topic,']").CountAsync();
        return eqCount + commaCount;
    }
}

// Output of a mod index scrape: topic-to-category maps for main and archived topics plus diagnostic info.
public sealed class ModIndexCategoriesResult
{
    public Dictionary<int, string> MainTopicCategoryMap { get; set; } = new();
    public Dictionary<int, string> ArchivedTopicCategoryMap { get; set; } = new();
    public HashSet<string> MainCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> UnknownLegacyCategories { get; set; } = [];
}

