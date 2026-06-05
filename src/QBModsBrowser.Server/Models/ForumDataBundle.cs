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
    // Canonical value lives in app-config.json — do not set a default here.
    public string RemoteRawUrl { get; set; } = "";

    // How many hours between remote-fetch checks on client machines.
    public double FetchIntervalHours { get; set; } = 6;

    // Predefined source options shown in the control-panel dropdown.
    // Each entry may carry an optional Warning label displayed alongside the URL.
    public List<RemoteRawUrlOption> RemoteRawUrlOptions { get; set; } = [];
}

// One selectable entry in the remote source dropdown.
public class RemoteRawUrlOption
{
    public string Url { get; set; } = "";
    // Optional warning tag shown in the dropdown (e.g. "Deprecated"). Null means no warning.
    public string? Warning { get; set; }
}
