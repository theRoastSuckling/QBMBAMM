using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using QBModsBrowser.Scraper.Models;

namespace QBModsBrowser.Server.Services;

/// <summary>Forum spoiler blocks (<c>sp-wrap</c>): exclude links used for assumed downloads and archive hints.</summary>
public static class SpoilerLinkFilter
{
    public static List<LinkRef> FilterOutSpoilerLinks(string? contentHtml, List<LinkRef> links)
    {
        if (links.Count == 0 || string.IsNullOrWhiteSpace(contentHtml))
            return links;

        var spoilerRanges = FindSpoilerRanges(contentHtml);
        if (spoilerRanges.Count == 0) return links;

        var result = new List<LinkRef>(links.Count);
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url))
            {
                result.Add(link);
                continue;
            }

            if (!IsHrefInsideSpoilerRanges(contentHtml, link.Url, spoilerRanges))
                result.Add(link);
        }

        return result;
    }

    /// <summary>True if this URL appears as an <c>href</c> inside a spoiler block (double/single quotes, <c>&amp;</c>).</summary>
    public static bool IsHrefInsideSpoilerRanges(string html, string url, List<(int Start, int End)> spoilerRanges)
    {
        if (spoilerRanges.Count == 0) return false;

        var variants = new HashSet<string>(StringComparer.Ordinal)
        {
            url,
            WebUtility.HtmlDecode(url)
        };
        if (url.Contains('&', StringComparison.Ordinal))
            variants.Add(url.Replace("&", "&amp;", StringComparison.Ordinal));

        foreach (var u in variants)
        {
            if (string.IsNullOrEmpty(u)) continue;
            var escaped = Regex.Escape(u);
            foreach (var pattern in new[]
                     {
                         $@"href\s*=\s*""{escaped}""",
                         $@"href\s*=\s*'{escaped}'"
                     })
            {
                foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
                {
                    if (spoilerRanges.Any(r => m.Index >= r.Start && m.Index < r.End))
                        return true;
                }
            }
        }

        return false;
    }

    public static List<(int Start, int End)> FindSpoilerRanges(string html)
    {
        var ranges = new List<(int Start, int End)>();
        var tagMatches = Regex.Matches(html, @"</?div\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        int spoilerStart = -1;
        int depth = 0;

        foreach (Match m in tagMatches)
        {
            var tag = m.Value;
            var isClose = tag.StartsWith("</div", StringComparison.OrdinalIgnoreCase);

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
