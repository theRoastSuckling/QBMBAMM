using System.Text.RegularExpressions;
using QBModsBrowser.Scraper;
using QBModsBrowser.Scraper.Models;

namespace QBModsBrowser.Server.Services;

/// <summary>
/// Rewrites Fractal Softworks forum topic URLs to local <c>/mod.html?id=</c> links when that topic exists in the mods index.
/// </summary>
// Rewrites forum links to local mod pages when those topics exist in local index data.
public static class ForumLocalModLinkRewriter
{
    private static readonly Regex AnchorHrefDoubleRegex = new(
        @"<a(\s[^>]*?\bhref\s*=\s*"")([^""]+)""([^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorHrefSingleRegex = new(
        @"<a(\s[^>]*?\bhref\s*=')([^']+)(')([^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Rewrites both HTML anchor tags and parsed link objects for local navigation.
    public static void Apply(ModDetail detail, IReadOnlySet<int> knownTopicIds)
    {
        if (knownTopicIds.Count == 0)
            return;

        detail.ContentHtml = RewriteHtmlAnchors(detail.ContentHtml, knownTopicIds);

        foreach (var link in detail.Links)
        {
            if (string.IsNullOrEmpty(link.Url))
                continue;
            if (!ShouldRewriteHref(link.Url, knownTopicIds, out int tid))
                continue;
            link.Url = $"/mod.html?id={tid}";
            link.IsExternal = false;
        }
    }

    // Replaces forum hrefs in rendered HTML while preserving unrelated links unchanged.
    private static string RewriteHtmlAnchors(string html, IReadOnlySet<int> knownTopicIds)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        html = AnchorHrefDoubleRegex.Replace(html, m =>
        {
            string href = m.Groups[2].Value;
            if (!ShouldRewriteHref(href, knownTopicIds, out int tid))
                return m.Value;
            string newHref = $"/mod.html?id={tid}";
            return $"<a{m.Groups[1].Value}{newHref}\"{m.Groups[3].Value}>";
        });

        html = AnchorHrefSingleRegex.Replace(html, m =>
        {
            string href = m.Groups[2].Value;
            if (!ShouldRewriteHref(href, knownTopicIds, out int tid))
                return m.Value;
            string newHref = $"/mod.html?id={tid}";
            return $"<a{m.Groups[1].Value}{newHref}{m.Groups[3].Value}{m.Groups[4].Value}>";
        });

        return html;
    }

    // Validates whether an href targets a known forum topic that should map locally.
    private static bool ShouldRewriteHref(string href, IReadOnlySet<int> knownTopicIds, out int topicId)
    {
        topicId = 0;
        if (!ForumConstants.IsForumHosted(href))
            return false;
        if (!ForumConstants.TryExtractTopicId(href, out topicId) || topicId == 0)
            return false;
        return knownTopicIds.Contains(topicId);
    }
}
