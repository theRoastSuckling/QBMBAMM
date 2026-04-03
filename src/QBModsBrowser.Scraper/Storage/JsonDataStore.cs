using System.Text.Json;
using System.Text.Json.Serialization;
using QBModsBrowser.Scraper.Models;
using Serilog;

namespace QBModsBrowser.Scraper.Storage;

// Reads/writes scraped mod data, config, and lightweight aggregate stats on disk.
public class JsonDataStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _basePath;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<ModSummary>? _indexCache;
    private DataStats? _statsCache;
    private DateTime _statsCacheTime;
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(30);

    // Creates a data store rooted at the configured data directory.
    public JsonDataStore(ILogger logger, string basePath)
    {
        _log = logger.ForContext<JsonDataStore>();
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    public string BasePath => _basePath;

    // --- Mods Index ---

    // Loads the mod index cache from disk (or returns empty if missing).
    public async Task<List<ModSummary>> LoadIndex()
    {
        if (_indexCache != null)
            return _indexCache;

        string path = Path.Combine(_basePath, "mods-index.json");
        if (!File.Exists(path))
            return [];

        var json = await File.ReadAllTextAsync(path);
        _indexCache = JsonSerializer.Deserialize<List<ModSummary>>(json, JsonOpts) ?? [];
        return _indexCache;
    }

    // Persists the full mod index and refreshes related in-memory caches.
    public async Task SaveIndex(List<ModSummary> mods)
    {
        await _lock.WaitAsync();
        try
        {
            string path = Path.Combine(_basePath, "mods-index.json");
            var json = JsonSerializer.Serialize(mods, JsonOpts);
            await File.WriteAllTextAsync(path, json);
            _indexCache = mods;
            _statsCache = null;
            _log.Information("Saved mods index with {Count} entries", mods.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Mod Detail ---

    // Loads one topic detail JSON if it was previously scraped.
    public async Task<ModDetail?> LoadDetail(int topicId)
    {
        string path = Path.Combine(_basePath, "mods", topicId.ToString(), "detail.json");
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<ModDetail>(json, JsonOpts);
    }

    // Persists one scraped topic detail file.
    public async Task SaveDetail(ModDetail detail)
    {
        string dir = Path.Combine(_basePath, "mods", detail.TopicId.ToString());
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "detail.json");
        var json = JsonSerializer.Serialize(detail, JsonOpts);
        await File.WriteAllTextAsync(path, json);
        _log.Debug("Saved detail for topic {TopicId}", detail.TopicId);
    }

    // --- Scraper Config ---

    // Loads scraper runtime config, applying normalization defaults.
    public async Task<ScraperConfig> LoadConfig()
    {
        string path = Path.Combine(_basePath, "scraper-config.json");
        if (!File.Exists(path))
            return new ScraperConfig();

        var json = await File.ReadAllTextAsync(path);
        var config = JsonSerializer.Deserialize<ScraperConfig>(json, JsonOpts) ?? new ScraperConfig();
        NormalizeAutoScope(config);
        return config;
    }

    // Saves scraper runtime config to disk.
    public async Task SaveConfig(ScraperConfig config)
    {
        NormalizeAutoScope(config);
        string path = Path.Combine(_basePath, "scraper-config.json");
        var json = JsonSerializer.Serialize(config, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    // Normalizes auto-scope config so scheduled runs always use NewData.
    private static void NormalizeAutoScope(ScraperConfig config)
    {
        config.DefaultScope ??= new ScrapeScope();
        config.DefaultScope.Type = ScopeType.NewData;
        config.DefaultScope.MaxPages = null;
        config.DefaultScope.TopicIds = null;
    }

    // --- Stats ---

    // Computes total mods/images/storage stats with a short-lived cache.
    public async Task<DataStats> GetStats()
    {
        if (_statsCache != null && DateTime.UtcNow - _statsCacheTime < StatsCacheDuration)
            return _statsCache;

        var index = await LoadIndex();
        string modsDir = Path.Combine(_basePath, "mods");

        long totalSize = 0;
        int imageCount = 0;

        if (Directory.Exists(modsDir))
        {
            var files = Directory.GetFiles(modsDir, "*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                totalSize += new FileInfo(f).Length;
                if (IsImageFile(f)) imageCount++;
            }
        }

        _statsCache = new DataStats
        {
            TotalMods = index.Count,
            TotalImages = imageCount,
            TotalSizeBytes = totalSize
        };
        _statsCacheTime = DateTime.UtcNow;
        return _statsCache;
    }

    // Picks a representative thumbnail from local images or external fallbacks.
    public string? PickThumbnail(int topicId, List<ImageRef> images)
    {
        string imagesDir = Path.Combine(_basePath, "mods", topicId.ToString(), "images");

        foreach (var img in images)
        {
            if (string.IsNullOrEmpty(img.LocalPath)) continue;
            string filePath = Path.Combine(imagesDir, img.LocalPath);
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists && fi.Length >= 10_000)
                    return img.LocalPath;
            }
            catch { }
        }

        foreach (var img in images)
        {
            if (!string.IsNullOrEmpty(img.LocalPath)) continue;
            string url = img.OriginalUrl;
            if (url.Contains("shields.io", StringComparison.OrdinalIgnoreCase)) continue;
            if (url.Contains("loading.gif", StringComparison.OrdinalIgnoreCase)) continue;
            return "ext:" + url;
        }

        return null;
    }

    // Checks whether a file path extension is a recognized image type.
    private static bool IsImageFile(string path) =>
        ImageFormats.IsImageExtension(Path.GetExtension(path));
}

// Holds persisted scraper behavior and UI preference settings.
public class ScraperConfig
{
    public double AutoScrapeIntervalHours { get; set; }
    public ScrapeScope DefaultScope { get; set; } = new() { Type = ScopeType.NewData };
    public int DelayBetweenPagesMs { get; set; } = 1500;
    public int DelayBetweenTopicsMs { get; set; } = 1500;
    public bool DefaultSpoilersOpen { get; set; }

    // When true, links on mod detail pages open in a new tab. Null = same as true (default).
    public bool? OpenLinksInNewTab { get; set; }

    // When true, external images fetched through the proxy are saved to disk for reuse.
    public bool CacheExternalImages { get; set; } = true;

    // When true, INF-level entries are printed to the server console; false suppresses them (default).
    public bool ShowInfoConsoleLogs { get; set; } = false;
}

// Represents aggregate counts and storage usage for the scraped dataset.
public class DataStats
{
    public int TotalMods { get; set; }
    public int TotalImages { get; set; }
    public long TotalSizeBytes { get; set; }
    public string TotalSizeFormatted => TotalSizeBytes switch
    {
        < 1024 => $"{TotalSizeBytes} B",
        < 1024 * 1024 => $"{TotalSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalSizeBytes / (1024.0 * 1024):F1} MB",
        _ => $"{TotalSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}


