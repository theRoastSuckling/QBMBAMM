using System.Text.RegularExpressions;
using QBModsBrowser.Scraper.Models;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Post-processes scraped topic HTML: rewrites image src to local API paths and normalizes external links.
public class HtmlProcessor
{
    private readonly ILogger _log;

    public HtmlProcessor(ILogger logger)
    {
        _log = logger.ForContext<HtmlProcessor>();
    }

    /// <summary>
    /// Rewrites image src attributes in the HTML to point to local API paths,
    /// and adds target="_blank" to external links.
    /// </summary>
    public string ProcessHtml(string html, int topicId, List<ImageRef> images)
    {
        string processed = html;

        // Rewrite image sources to local API paths
        foreach (var img in images)
        {
            if (string.IsNullOrEmpty(img.LocalPath))
                continue;

            string apiPath = $"/api/images/{topicId}/{img.LocalPath}";
            processed = processed.Replace(img.OriginalUrl, apiPath);
        }

        // Add target="_blank" to external links that don't already have it
        processed = AddTargetBlankToExternalLinks(processed);

        // Remove SMF-specific edit/quote buttons and metadata that won't render well
        processed = StripSmfArtifacts(processed);

        return processed;
    }

    private static string AddTargetBlankToExternalLinks(string html)
    {
        string pattern = @"<a\s+([^>]*?)href=""(https?://(?!" + Regex.Escape(ForumConstants.ForumHost) + @")[^""]+)""([^>]*?)>";
        return Regex.Replace(html, pattern,
            match =>
            {
                string before = match.Groups[1].Value;
                string href = match.Groups[2].Value;
                string after = match.Groups[3].Value;
                string full = before + after;

                if (full.Contains("target=", StringComparison.OrdinalIgnoreCase))
                    return match.Value;

                return $@"<a {before}href=""{href}"" target=""_blank"" rel=""noopener""{after}>";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string StripSmfArtifacts(string html)
    {
        // Remove edit timestamps like "« Last Edit: ... »"
        html = Regex.Replace(html, @"<span class=""smalltext"">\s*&laquo;.*?&raquo;\s*</span>",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Replace smiley <img> tags with their alt text (original emoticon like 8) or ;D)
        html = Regex.Replace(html,
            @"<img\s+[^>]*?src=""[^""]*?/Smileys/[^""]*""[^>]*?>",
            match =>
            {
                var altMatch = Regex.Match(match.Value, @"alt=""([^""]*?)""", RegexOptions.IgnoreCase);
                return altMatch.Success ? altMatch.Groups[1].Value : "";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return html;
    }
}

