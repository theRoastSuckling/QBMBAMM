using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Server.Models;

namespace QBModsBrowser.Server.Models;

// Portable snapshot of all scraped forum data committed to QBForumModData and fetched by clients.
public class ForumDataBundle
{
    // Timestamp of the most recently scraped mod in the index (max of all ScrapedAt values).
    public DateTime UpdatedAt { get; set; }

    // Full mod index (equivalent to mods-index.json).
    public List<ModSummary> Index { get; set; } = [];

    // Full topic details keyed by TopicId (equivalent to mods/{id}/detail.json).
    public Dictionary<int, ModDetail> Details { get; set; } = new();

    // Resolved assumed-download candidates keyed by TopicId, sourced from assumed-downloads-cache.
    public Dictionary<int, List<AssumedDownloadCandidate>> AssumedDownloads { get; set; } = new();
}

// Configuration for the QBForumModData repo — publishing and remote-fetch settings.
public class ForumDataRepoConfig
{
    // Absolute path to the local clone of QBForumModData. Set only on the scraper-operator machine;
    // leave null/empty on regular client machines to disable publishing.
    public string? LocalRepoPath { get; set; }

    // Raw GitHub URL for the bundle JSON served to regular clients.
    public string RemoteRawUrl { get; set; } =
        "https://github.com/theRoastSuckling/QBForumModData/raw/refs/heads/main/forum-data-bundle.json";

    // How many hours between remote-fetch checks on client machines.
    public double FetchIntervalHours { get; set; } = 6;
}
