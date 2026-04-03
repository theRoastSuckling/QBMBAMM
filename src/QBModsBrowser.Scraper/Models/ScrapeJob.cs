namespace QBModsBrowser.Scraper.Models;

public enum ScrapeState
{
    Idle,
    Scraping,
    Completed,
    Failed,
    Cancelled
}

public enum ScopeType
{
    NewData,
    All,
    Pages,
    Topics,
    /// <summary>Only the Modding Resources board (board 9), filtered titles, category <c>library</c>.</summary>
    LibrariesOnly
}

/// <summary>Flags controlling which forum boards are included in a scrape run.</summary>
[Flags]
public enum ScrapeBoards
{
    None = 0,
    /// <summary>Main mods board (board 8).</summary>
    Main = 1,
    /// <summary>Lesser mods board (board 3).</summary>
    Lesser = 2,
    /// <summary>Modding Resources / libraries board (board 9).</summary>
    Libraries = 4
}

// Parameters that control what a scrape run covers: scope type, page limit, topic list, and boards.
public class ScrapeScope
{
    public ScopeType Type { get; set; } = ScopeType.All;
    public int? MaxPages { get; set; }
    public List<int>? TopicIds { get; set; }
    /// <summary>Which boards to include; defaults to Main+Libraries (current behavior). Ignored for LibrariesOnly and Topics.</summary>
    public ScrapeBoards Boards { get; set; } = ScrapeBoards.Main | ScrapeBoards.Libraries;
}

// Mutable state for the running or most-recent scrape job, polled by the UI for progress.
public class ScrapeJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ScrapeState State { get; set; } = ScrapeState.Idle;
    public ScrapeScope Scope { get; set; } = new();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int TotalTopics { get; set; }
    public int ProcessedTopics { get; set; }
    public int TotalImages { get; set; }
    public int DownloadedImages { get; set; }
    public int Errors { get; set; }
    /// <summary>Per-topic title or label while scraping threads.</summary>
    public string? CurrentItem { get; set; }
    /// <summary>
    /// Coarse step before <see cref="TotalTopics"/> is known (e.g. mod index, board listing).
    /// Avoids the UI looking idle during long preamble work.
    /// </summary>
    public string? CurrentPhase { get; set; }
    public string? ErrorMessage { get; set; }

    public double ProgressPercent => TotalTopics > 0
        ? Math.Round(100.0 * ProcessedTopics / TotalTopics, 1)
        : 0;

    public string? Duration => StartedAt.HasValue
        ? ((FinishedAt ?? DateTime.UtcNow) - StartedAt.Value).ToString(@"hh\:mm\:ss")
        : null;
}

// Summary returned when a scrape finishes, capturing counts and any terminal error.
public class ScrapeResult
{
    public bool Success { get; set; }
    public int ModsScraped { get; set; }
    public int ImagesDownloaded { get; set; }
    public int Errors { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
}

