using System.Text.Json.Serialization;

namespace QBModsBrowser.Server.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadStatus
{
    Queued,
    RetrievingInfo,
    Downloading,
    Installing,
    Completed,
    Failed,
    Canceled
}

// Tracks a single mod download through the queue: URL, progress, status, and install result.
public class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; set; } = "";
    public string ModName { get; set; } = "";
    public int? TopicId { get; set; }
    public string? GameVersion { get; set; }
    public string? PreviousGameVersion { get; set; }
    public string? ModVersion { get; set; }
    public string? PreviousModVersion { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? Math.Round(100.0 * DownloadedBytes / TotalBytes, 1) : 0;
    public string? FileName { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ArchiveFileName { get; set; }
    public List<string> InstalledModIds { get; set; } = [];
}

