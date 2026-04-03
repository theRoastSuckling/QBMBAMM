using QBModsBrowser.Scraper;
using QBModsBrowser.Scraper.Models;

namespace QBModsBrowser.Server.Models;

// API card shape merging scraped forum data with ModRepo, local install, version-check, and download enrichment.
public class EnrichedModSummary
{
    // Original ModSummary fields
    public int TopicId { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public bool InModIndex { get; set; }
    public bool IsArchivedModIndex { get; set; }
    public string? GameVersion { get; set; }
    public string Author { get; set; } = "";
    public int Replies { get; set; }
    public int Views { get; set; }
    public string? CreatedDate { get; set; }
    public string? LastPostDate { get; set; }
    public string? LastPostBy { get; set; }
    public string TopicUrl { get; set; } = "";
    public string? ThumbnailPath { get; set; }
    public DateTime ScrapedAt { get; set; }

    // ModRepo enrichment
    public string? ModRepoName { get; set; }
    public string? DirectDownloadUrl { get; set; }
    public string? DownloadPageUrl { get; set; }
    public string? ModRepoVersion { get; set; }

    // Local mod enrichment
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public string? LocalModId { get; set; }
    public string? LocalModName { get; set; }
    public string? LocalVersion { get; set; }
    public string? LocalGameVersion { get; set; }

    // Version check enrichment
    public string? OnlineVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool HasDirectDownload { get; set; }
    public string? UpdateDownloadUrl { get; set; }

    // True when any locally installed version is older than any known remote version:
    // version checker detected a newer mod version, the forum/index targets a higher
    // game version than what is installed locally, or the mod index lists a higher
    // mod version than what is installed locally.
    public bool HasAnyVersionDifference =>
        UpdateAvailable ||
        (IsInstalled && LocalGameVersion != null && GameVersion != null &&
         GameVersionComparer.Compare(LocalGameVersion, GameVersion) < 0) ||
        (IsInstalled && LocalVersion != null && ModRepoVersion != null &&
         GameVersionComparer.Compare(LocalVersion, ModRepoVersion) < 0);

    // Assumed download enrichment
    public bool HasAssumedDownload { get; set; }
    public int AssumedDownloadCount { get; set; }
    public string? AssumedConfidence { get; set; }
    public bool IsAssumedPatreonLink { get; set; }
    // True when the single assumed candidate requires the user to manually pick/download (e.g. GitHub releases page).
    public bool AssumedRequiresManualStep { get; set; }

    // Multi-mod support
    public List<LocalModInfo>? AdditionalLocalMods { get; set; }

    public static EnrichedModSummary FromSummary(ModSummary s)
    {
        return new EnrichedModSummary
        {
            TopicId = s.TopicId,
            Title = s.Title,
            Category = s.Category,
            InModIndex = s.InModIndex,
            IsArchivedModIndex = s.IsArchivedModIndex,
            GameVersion = s.GameVersion,
            Author = s.Author,
            Replies = s.Replies,
            Views = s.Views,
            CreatedDate = s.CreatedDate,
            LastPostDate = s.LastPostDate,
            LastPostBy = s.LastPostBy,
            TopicUrl = s.TopicUrl,
            ThumbnailPath = s.ThumbnailPath,
            ScrapedAt = s.ScrapedAt
        };
    }
}

// Compact view of a locally installed mod used when multiple mods map to one forum topic.
public class LocalModInfo
{
    public string ModId { get; set; } = "";
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? GameVersion { get; set; }
    public bool IsEnabled { get; set; }
}

