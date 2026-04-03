namespace QBModsBrowser.Server.Models;

// A resolved download candidate extracted from a forum post link, with confidence and manual-step flags.
public class AssumedDownloadCandidate
{
    public string OriginalUrl { get; set; } = "";
    public string? ResolvedDirectUrl { get; set; }
    public string SourceHost { get; set; } = "";
    public string? FileName { get; set; }
    public string Confidence { get; set; } = "medium";
    public bool RequiresManualStep { get; set; }
    public string? LinkText { get; set; }
}

