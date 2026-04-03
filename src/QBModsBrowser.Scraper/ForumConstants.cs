using System.Text.RegularExpressions;
using QBModsBrowser.Scraper.Models;

namespace QBModsBrowser.Scraper;

// Centralizes forum URLs, category aliases, and parsing helpers used by scraper/server.
public static partial class ForumConstants
{
    public const string ForumHost = "fractalsoftworks.com";
    public const string UncategorizedCategory = "uncategorized";
    public const string BoardUrl = $"https://{ForumHost}/forum/index.php?board=8.";

    /// <summary>Lesser mods board — community uploads not listed in the official mod index.</summary>
    public const string LesserBoardUrl = $"https://{ForumHost}/forum/index.php?board=3.";

    /// <summary>Maximum pages to scrape from the lesser board regardless of scope.</summary>
    public const int LesserBoardMaxPages = 20;

    /// <summary>Modding Resources board (sorted by last post desc when scraping).</summary>
    public const string LibraryBoardUrl = $"https://{ForumHost}/forum/index.php?board=9.";

    /// <summary>Matches the mod-index style label and category filter ordering in the web UI.</summary>
    public const string LibraryCategory = "libraries";

    // Builds a canonical forum topic URL for a known topic id.
    public static string TopicUrl(int topicId) => $"https://{ForumHost}/forum/index.php?topic={topicId}.0";

    // Checks whether a board base URL points to the Modding Resources board.
    public static bool IsLibraryBoardBase(string? boardBaseUrl) =>
        !string.IsNullOrEmpty(boardBaseUrl) &&
        boardBaseUrl.StartsWith(LibraryBoardUrl, StringComparison.OrdinalIgnoreCase);

    // Detects titles that look like versioned library/resource threads.
    public static bool IsLibraryThreadTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return false;
        var t = title.TrimStart();
        if (t.Length < 2 || t[0] != '[')
            return false;
        int i = 1;
        while (i < t.Length && char.IsWhiteSpace(t[i]))
            i++;
        return i < t.Length && char.IsDigit(t[i]);
    }

    // Accepts legacy and current names for the libraries category.
    public static bool IsLibraryCategoryName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return false;
        var c = category.Trim().ToLowerInvariant();
        return c is "library" or "libraries";
    }

    // Normalizes old/new utility category labels to one semantic group.
    public static bool IsStandaloneUtilityCategoryName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return false;
        var c = category.Trim();
        if (c.Contains("standalone utilit", StringComparison.OrdinalIgnoreCase))
            return true;
        return c.Equals("Utility mods", StringComparison.OrdinalIgnoreCase);
    }

    // Returns true for links hosted on the Fractal Softworks forum domain.
    public static bool IsForumHosted(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, ForumHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + ForumHost, StringComparison.OrdinalIgnoreCase);
    }

    // Extracts topic id from forum-style hrefs and returns parse success.
    public static bool TryExtractTopicId(string? href, out int topicId)
    {
        topicId = 0;
        if (string.IsNullOrWhiteSpace(href))
            return false;

        var match = TopicIdRegex().Match(href);
        return match.Success && int.TryParse(match.Groups[1].Value, out topicId);
    }

    // Captures bracketed game versions, including mixed tags like [0.96a/SEMI-ABANDONED].
    [GeneratedRegex(@"\[(\d+\.\d+[\w.\-]*)(?:[^\]]*)\]")]
    public static partial Regex GameVersionRegex();

    // Captures topic ids from common forum URL formats.
    [GeneratedRegex(@"(?:topic[=,\/])(\d+)", RegexOptions.IgnoreCase)]
    public static partial Regex TopicIdRegex();

    // Returns true when the title contains "WIP" (case-insensitive).
    public static bool IsWipTitle(string? title) =>
        !string.IsNullOrEmpty(title) &&
        title.Contains("WIP", StringComparison.OrdinalIgnoreCase);

    // Accepts lesser-board topics that carry a version tag and aren't MOVED stubs.
    public static bool IsLesserBoardTopicTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;
        if (title.Contains("MOVED", StringComparison.OrdinalIgnoreCase))
            return false;
        return GameVersionRegex().IsMatch(title);
    }

    // Guesses a category for board-3 topics using keywords in the title.
    public static string GuessCategoryFromTitle(string title)
    {
        if (title.Contains("faction", StringComparison.OrdinalIgnoreCase))
            return "factions";
        if (title.Contains("portrait", StringComparison.OrdinalIgnoreCase))
            return "portraits";
        if (title.Contains("flag", StringComparison.OrdinalIgnoreCase))
            return "flags";
        return UncategorizedCategory;
    }

    // True when host is YouTube / Shorts (board-3 filter excludes these as non-download pointers).
    private static bool IsYoutubeHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    // True when the first post has any http(s) link that is not forum-internal, Nexus Mods, or YouTube (board-3 quality gate).
    public static bool HasFileHostingLinks(IEnumerable<LinkRef> links)
    {
        foreach (var l in links)
        {
            if (string.IsNullOrWhiteSpace(l.Url))
                continue;
            if (!Uri.TryCreate(l.Url.Trim(), UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                continue;
            if (IsForumHosted(l.Url))
                continue;
            var host = uri.Host;
            if (host.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsYoutubeHost(host))
                continue;
            return true;
        }

        return false;
    }
}


