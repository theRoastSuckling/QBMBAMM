using Microsoft.Playwright;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Detects and waits for Cloudflare challenge pages before the scraper proceeds.
public static class CloudflareHelper
{
    /// <summary>
    /// Waits for Cloudflare challenge to resolve. The persistent browser profile
    /// should have cf_clearance cookies from a previous session. If the challenge
    /// appears, it will wait up to the timeout for it to resolve (auto or manual).
    /// </summary>
    /// <remarks>
    /// Do not match generic substrings like "security" or "moment" in the page title:
    /// normal forum topic titles often contain those words and would be mistaken for a challenge.
    /// </remarks>
    public static async Task WaitForCloudflare(IPage page, ILogger log, int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < timeoutSeconds / 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var title = await page.TitleAsync();
            if (!LooksLikeCloudflareChallengeTitle(title))
                return;

            if (i == 0)
                log.Warning("Cloudflare challenge detected - waiting for resolution...");

            await Task.Delay(2000, cancellationToken);
        }

        var finalTitle = await page.TitleAsync();
        if (LooksLikeCloudflareChallengeTitle(finalTitle))
        {
            log.Error("Cloudflare challenge not resolved after {Timeout}s. Title: {Title}",
                timeoutSeconds, finalTitle);
            throw new InvalidOperationException(
                $"Cloudflare challenge not resolved. Launch the browser profile manually and pass the challenge first.");
        }
    }

    /// <summary>
    /// Heuristic for Cloudflare interstitial pages only — must not match normal forum topic titles.
    /// </summary>
    private static bool LooksLikeCloudflareChallengeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            return true;

        if (title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase))
            return true;

        if (title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
            return true;

        // Rare short interstitial titles
        if (title.Equals("Cloudflare", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

