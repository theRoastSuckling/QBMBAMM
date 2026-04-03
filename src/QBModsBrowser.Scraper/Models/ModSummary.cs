namespace QBModsBrowser.Scraper.Models;

// Lightweight per-topic record populated from board listing rows and mod-index metadata.
public class ModSummary
{
    public int TopicId { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = ForumConstants.UncategorizedCategory;
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
    /// <summary>True when "WIP" appears in the topic title.</summary>
    public bool IsWip { get; set; }
    /// <summary>Board number the topic was scraped from (8=main, 3=lesser, 9=libraries).</summary>
    public int? SourceBoard { get; set; }
}

