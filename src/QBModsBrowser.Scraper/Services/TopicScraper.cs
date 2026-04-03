using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using QBModsBrowser.Scraper.Models;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Scrapes a single forum topic to extract OP content, images, links, and metadata.
public class TopicScraper
{
    private readonly ILogger _log;
    private readonly int _delayMs;

    public TopicScraper(ILogger logger, int delayBetweenTopicsMs = 1500)
    {
        _log = logger.ForContext<TopicScraper>();
        _delayMs = delayBetweenTopicsMs;
    }

    public async Task<ModDetail?> ScrapeTopic(IPage page, int topicId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string url = ForumConstants.TopicUrl(topicId);
        _log.Information("Scraping topic {TopicId}: {Url}", topicId, url);

        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await CloudflareHelper.WaitForCloudflare(page, _log, cancellationToken: ct);
            await Task.Delay(_delayMs, ct);

            // SMF structure: #forumposts > form#quickModForm > div.bordercolor > div.windowbg (first = OP post)
            // Each post has: div.poster (author), div.postarea (content)
            var firstPost = page.Locator("#forumposts .windowbg, #forumposts .windowbg2").First;
            if (await firstPost.CountAsync() == 0)
            {
                _log.Warning("No posts found on topic {TopicId}", topicId);
                return null;
            }

            // Resolve lazy-loaded images before extracting content.
            // SMF sets src=loading.gif and puts the real URL in data-imageurl or alt.
            await ResolveLazyImages(page);

            string title = await ExtractTitle(page);
            var versionMatch = ForumConstants.GameVersionRegex().Match(title);

            var (author, authorTitle, authorPostCount, avatarUrl) = await ExtractAuthorInfo(firstPost);
            string? postDate = await ExtractPostDate(firstPost);
            string contentHtml = await ExtractContentHtml(firstPost);
            string? lastEditDate = await ExtractLastEditDate(firstPost, contentHtml);
            var images = ExtractImageUrls(contentHtml);
            var links = ExtractLinks(contentHtml);

            return new ModDetail
            {
                TopicId = topicId,
                Title = title,
                GameVersion = versionMatch.Success ? versionMatch.Groups[1].Value : null,
                Author = author,
                AuthorTitle = authorTitle,
                AuthorPostCount = authorPostCount,
                AuthorAvatarPath = avatarUrl,
                PostDate = postDate,
                LastEditDate = lastEditDate,
                ContentHtml = contentHtml,
                Images = images,
                Links = links,
                ScrapedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Failed to scrape topic {TopicId}", topicId);
            return null;
        }
    }

    private static async Task ResolveLazyImages(IPage page)
    {
        // Scroll to bottom and back to trigger any viewport-based lazy loaders
        await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
        await Task.Delay(500);
        await page.EvaluateAsync("window.scrollTo(0, 0)");
        await Task.Delay(300);

        // Fix remaining lazy images: SMF uses src=loading.gif with real URL in alt or data attributes
        await page.EvaluateAsync(@"
            document.querySelectorAll('img').forEach(img => {
                const src = img.getAttribute('src') || '';
                if (!src.includes('loading.gif') && !src.includes('loading_sm.gif')) return;

                // Try data-imageurl first, then alt text if it looks like a URL
                const realUrl = img.getAttribute('data-imageurl')
                    || img.getAttribute('data-src')
                    || img.getAttribute('data-original');

                if (realUrl) {
                    img.setAttribute('src', realUrl);
                    return;
                }

                const alt = img.getAttribute('alt') || '';
                if (alt.match(/^https?:\/\/.+\.(png|jpg|jpeg|gif|webp|bmp|svg)/i)) {
                    img.setAttribute('src', alt);
                    img.setAttribute('alt', '');
                }
            });
        ");
    }

    private static async Task<string> ExtractTitle(IPage page)
    {
        // SMF: <span id="top_subject">Topic: [0.98a] Title... (Read N times)</span>
        var topSubject = page.Locator("#top_subject");
        if (await topSubject.CountAsync() > 0)
        {
            string raw = await topSubject.InnerTextAsync();
            raw = raw.Replace("Topic:", "").Trim();
            // Strip "(Read N times)" suffix
            int readIdx = raw.IndexOf("(Read", StringComparison.OrdinalIgnoreCase);
            if (readIdx > 0) raw = raw[..readIdx].Trim();
            // Strip "\u00a0" (nbsp) 
            raw = raw.Replace("\u00a0", " ").Trim();
            return raw;
        }

        // Fallback: first h5 subject link
        var h5Link = page.Locator("h5[id^='subject_'] a").First;
        if (await h5Link.CountAsync() > 0)
            return (await h5Link.InnerTextAsync()).Trim();

        string pageTitle = await page.TitleAsync();
        return pageTitle;
    }

    private static async Task<(string author, string? authorTitle, int postCount, string? avatarUrl)>
        ExtractAuthorInfo(ILocator firstPost)
    {
        string author = "";
        string? authorTitle = null;
        int postCount = 0;
        string? avatarUrl = null;

        var poster = firstPost.Locator("div.poster");

        // Author name: div.poster > h4 > a
        var authorLink = poster.Locator("h4 a");
        if (await authorLink.CountAsync() > 0)
            author = (await authorLink.First.InnerTextAsync()).Trim();

        // Author info is in ul > li items
        var listItems = poster.Locator("ul li");
        int liCount = await listItems.CountAsync();
        for (int i = 0; i < liCount; i++)
        {
            var li = listItems.Nth(i);
            string text = (await li.InnerTextAsync()).Trim();

            // First plain text li (not containing images/links) is the rank
            if (i == 0 && !string.IsNullOrEmpty(text) && !text.StartsWith("Posts:"))
                authorTitle = text;

            if (text.StartsWith("Posts:", StringComparison.OrdinalIgnoreCase))
            {
                var numMatch = Regex.Match(text, @"[\d,]+");
                if (numMatch.Success)
                    int.TryParse(numMatch.Value.Replace(",", ""), out postCount);
            }
        }

        // Avatar image
        var avatar = poster.Locator("img.avatar");
        if (await avatar.CountAsync() > 0)
            avatarUrl = await avatar.First.GetAttributeAsync("src");

        return (author, authorTitle, postCount, avatarUrl);
    }

    private static async Task<string?> ExtractPostDate(ILocator firstPost)
    {
        // SMF: .keyinfo .smalltext contains "« on: August 07, 2024, 01:00:20 PM »"
        // (Playwright now returns proper Unicode «/» instead of the old mojibake Â«/Â»)
        var dateEl = firstPost.Locator(".keyinfo .smalltext");
        if (await dateEl.CountAsync() > 0)
        {
            string raw = await dateEl.First.InnerTextAsync();
            var dateMatch = Regex.Match(raw, @"on:\s*(.+?)\s*(?:Â»|»)");
            if (dateMatch.Success)
                return dateMatch.Groups[1].Value.Trim();
            return raw.Trim();
        }
        return null;
    }

    private static async Task<string> ExtractContentHtml(ILocator firstPost)
    {
        // SMF: div.postarea > ... > div.post > ... > div.inner#msg_XXXXX
        var innerDiv = firstPost.Locator("div.post div.inner");
        if (await innerDiv.CountAsync() > 0)
            return await innerDiv.First.InnerHTMLAsync();

        // Fallback
        var postDiv = firstPost.Locator("div.post");
        if (await postDiv.CountAsync() > 0)
            return await postDiv.First.InnerHTMLAsync();

        return await firstPost.InnerHTMLAsync();
    }

    private static List<ImageRef> ExtractImageUrls(string html)
    {
        var images = new List<ImageRef>();
        var imgMatches = Regex.Matches(html, @"<img[^>]+src=""([^""]+)""[^>]*/?>", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in imgMatches)
        {
            string src = m.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(src) || src.StartsWith("data:"))
                continue;

            // Skip smiley images and tiny UI images
            if (src.Contains("/Smileys/", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("/icons/", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("star.gif", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(src)) continue;

            string? alt = null;
            var altMatch = Regex.Match(m.Value, @"alt=""([^""]*)""", RegexOptions.IgnoreCase);
            if (altMatch.Success) alt = altMatch.Groups[1].Value;

            images.Add(new ImageRef { OriginalUrl = src, LocalPath = "", Alt = alt });
        }

        return images;
    }

    private static async Task<string?> ExtractLastEditDate(ILocator firstPost, string html)
    {
        static string? ParseLastEdit(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;

            // Example: « Last Edit: October 29, 2025, 04:40:33 PM by confer0 »
            // Also handles legacy Â» mojibake and HTML-entity &raquo; forms.
            var match = Regex.Match(
                source,
                @"Last\s+Edit:\s*(.+?)(?:\s*(?:Â»|»|&raquo;)|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return null;

            var value = Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        // 1) Most reliable for SMF: smalltext metadata in the post container
        var smallText = firstPost.Locator("span.smalltext");
        int smallCount = await smallText.CountAsync();
        for (int i = 0; i < smallCount; i++)
        {
            var text = await smallText.Nth(i).InnerTextAsync();
            var parsed = ParseLastEdit(text);
            if (!string.IsNullOrEmpty(parsed))
                return parsed;
        }

        // 2) Fallback: parsed content html (handles entity-encoded forms)
        var parsedFromHtml = ParseLastEdit(html);
        if (!string.IsNullOrEmpty(parsedFromHtml))
            return parsedFromHtml;

        // 3) Last fallback: full post text
        var fullText = await firstPost.InnerTextAsync();
        return ParseLastEdit(fullText);
    }

    // Extracts non-spoiler links and decodes HTML entities so stored URLs stay navigable.
    private static List<LinkRef> ExtractLinks(string html)
    {
        var links = new List<LinkRef>();
        var spoilerRanges = FindSpoilerRanges(html);
        var linkMatches = Regex.Matches(html, @"<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in linkMatches)
        {
            if (spoilerRanges.Any(r => m.Index >= r.Start && m.Index < r.End))
                continue;

            string href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#") || href.StartsWith("javascript:"))
                continue;

            if (!seen.Add(href)) continue;

            bool isExternal = !ForumConstants.IsForumHosted(href);

            links.Add(new LinkRef { Url = href, Text = text, IsExternal = isExternal });
        }

        return links;
    }

    private static List<(int Start, int End)> FindSpoilerRanges(string html)
    {
        var ranges = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(html)) return ranges;

        var tagMatches = Regex.Matches(html, @"</?div\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        int spoilerStart = -1;
        int depth = 0;

        foreach (Match m in tagMatches)
        {
            var tag = m.Value;
            bool isClose = tag.StartsWith("</div", StringComparison.OrdinalIgnoreCase);

            if (spoilerStart < 0)
            {
                if (isClose) continue;
                if (!tag.Contains("sp-wrap", StringComparison.OrdinalIgnoreCase)) continue;

                spoilerStart = m.Index;
                depth = 1;
                continue;
            }

            if (isClose) depth--;
            else depth++;

            if (depth == 0)
            {
                ranges.Add((spoilerStart, m.Index + m.Length));
                spoilerStart = -1;
            }
        }

        return ranges;
    }
}


