namespace QBModsBrowser.Scraper.Models;

// Full scraped topic record including OP HTML, images, links, and author metadata.
public class ModDetail
{
    public int TopicId { get; set; }
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public string? GameVersion { get; set; }
    public string Author { get; set; } = "";
    public string? AuthorTitle { get; set; }
    public int AuthorPostCount { get; set; }
    public string? AuthorAvatarPath { get; set; }
    public string? PostDate { get; set; }
    public string? LastEditDate { get; set; }
    public string ContentHtml { get; set; } = "";
    public List<ImageRef> Images { get; set; } = [];
    public List<LinkRef> Links { get; set; } = [];
    public DateTime ScrapedAt { get; set; }

    /// <summary>True when this response was built from the mods index only because <c>detail.json</c> was missing.</summary>
    public bool IsPlaceholderDetail { get; set; }
}

// Maps a scraped image's original URL to its downloaded local file path.
public class ImageRef
{
    public string OriginalUrl { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string? Alt { get; set; }
}

// Represents a hyperlink extracted from the topic OP with its display text and origin flag.
public class LinkRef
{
    public string Url { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsExternal { get; set; }
}

