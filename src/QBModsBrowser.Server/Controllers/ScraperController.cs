using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/scraper")]
// Exposes scraper status, start/stop, config, log, Playwright install, remote-data-info, and force-refresh endpoints for the UI control panel.
public class ScraperController : ControllerBase
{
    private readonly ScraperOrchestrator _orchestrator;
    private readonly JsonDataStore _store;
    private readonly IConfiguration _config;
    private readonly PlaywrightService _playwright;
    private readonly ForumDataFetchService _forumFetch;

    // Serilog.ILogger is not registered in DI (passed manually to singletons), so use the static Log directly.
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ScraperController>();

    // Accepts ForumDataFetchService to support the force-refresh-remote-data endpoint.
    public ScraperController(
        ScraperOrchestrator orchestrator,
        JsonDataStore store,
        IConfiguration config,
        PlaywrightService playwright,
        ForumDataFetchService forumFetch)
    {
        _orchestrator = orchestrator;
        _store = store;
        _config = config;
        _playwright = playwright;
        _forumFetch = forumFetch;
    }

    // Returns scraper state, stats, and Playwright installation status.
    // Playwright check is included here so the UI always has a fresh value from the first poll,
    // regardless of when the control panel is opened.
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var job = _orchestrator.CurrentJob;
        var lastResult = _orchestrator.LastResult;
        var stats = await _store.GetStats();

        return Ok(new
        {
            state = job.State.ToString().ToLowerInvariant(),
            isScraping = _orchestrator.IsScraping,
            isPlaywrightInstalled = _playwright.IsInstalled(),
            playwrightBrowserStatus = _playwright.GetBrowserStatus(),
            job = new
            {
                job.Id,
                job.State,
                job.StartedAt,
                job.FinishedAt,
                job.Duration,
                job.TotalTopics,
                job.ProcessedTopics,
                job.TotalImages,
                job.DownloadedImages,
                job.Errors,
                job.CurrentItem,
                job.CurrentPhase,
                job.ProgressPercent,
                job.ErrorMessage
            },
            lastResult = lastResult != null ? new
            {
                lastResult.Success,
                lastResult.ModsScraped,
                lastResult.ImagesDownloaded,
                lastResult.Errors,
                duration = lastResult.Duration.ToString(@"hh\:mm\:ss"),
                lastResult.ErrorMessage
            } : null,
            stats = new
            {
                stats.TotalMods,
                stats.TotalImages,
                stats.TotalSizeFormatted
            }
        });
    }

    // StartRequest carries scope, per-board page counts, topic IDs, and board selection flags.
    public class StartRequest
    {
        public string Scope { get; set; } = "all";
        // Per-board page limits for scope=pages; each defaults to scraping all pages when omitted.
        public int? PagesMain      { get; set; }
        public int? PagesLesser    { get; set; }
        public int? PagesLibraries { get; set; }
        public List<int>? TopicIds { get; set; }
        /// <summary>Which boards to include: "main", "lesser", "libraries". Null/empty defaults to Main+Libraries.</summary>
        public List<string>? Boards { get; set; }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest request)
    {
        var scope = new ScrapeScope();

        switch (request.Scope.ToLowerInvariant())
        {
            case "new":
            case "newdata":
                scope.Type = ScopeType.NewData;
                break;
            case "pages":
                scope.Type = ScopeType.Pages;
                scope.MaxPagesMain      = request.PagesMain;
                scope.MaxPagesLesser    = request.PagesLesser;
                scope.MaxPagesLibraries = request.PagesLibraries;
                break;
            case "topics":
                scope.Type = ScopeType.Topics;
                scope.TopicIds = request.TopicIds ?? [];
                break;
            case "libraries":
            case "librariesonly":
            case "library":
                scope.Type = ScopeType.LibrariesOnly;
                break;
            default:
                scope.Type = ScopeType.All;
                break;
        }

        // Parse board flags; default to Main+Libraries when not specified.
        if (request.Boards != null && request.Boards.Count > 0)
        {
            scope.Boards = ScrapeBoards.None;
            foreach (var b in request.Boards.Select(s => s.Trim().ToLowerInvariant()))
            {
                if (b == "main") scope.Boards |= ScrapeBoards.Main;
                else if (b == "lesser") scope.Boards |= ScrapeBoards.Lesser;
                else if (b == "libraries") scope.Boards |= ScrapeBoards.Libraries;
            }
        }

        bool started = await _orchestrator.StartScrape(scope);
        if (!started)
            return Conflict(new { error = "A scrape is already in progress" });

        return Ok(new { message = "Scrape started", scope = scope.Type.ToString() });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _orchestrator.StopScrape();
        return Ok(new { message = "Stop signal sent" });
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var config = await _store.LoadConfig();
        return Ok(config);
    }

    // Persists updated scraper config.
    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] ScraperConfig config)
    {
        await _store.SaveConfig(config);
        return Ok(config);
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int lines = 100, [FromQuery] string type = "scraper")
    {
        // Use the pre-resolved path stored at startup; avoids CWD-relative mismatches when running as an exe.
        string logPath = _config["ResolvedLogPath"]
            ?? Path.GetFullPath(_config["LogPath"] ?? "../../logs", AppContext.BaseDirectory);

        string prefix = type == "server" ? "server-" : "scraper-";
        var logFiles = Directory.Exists(logPath)
            ? Directory.GetFiles(logPath, $"{prefix}*.log")
                .OrderByDescending(f => f)
                .ToArray()
            : [];

        if (logFiles.Length == 0)
            return Ok(new { lines = Array.Empty<string>() });

        // Read with FileShare.ReadWrite to avoid conflicts with Serilog's file sink
        using var fs = new FileStream(logFiles[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        int maxLines = Math.Max(1, lines);
        var allLines = new Queue<string>(maxLines);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (allLines.Count == maxLines)
                allLines.Dequeue();
            allLines.Enqueue(line);
        }
        var tail = allLines.ToArray();

        return Ok(new { file = Path.GetFileName(logFiles[0]), lines = tail });
    }

    [HttpPost("backfill-thumbnails")]
    public async Task<IActionResult> BackfillThumbnails()
    {
        var index = await _store.LoadIndex();
        int updated = 0;

        foreach (var summary in index)
        {
            if (!string.IsNullOrEmpty(summary.ThumbnailPath)) continue;

            var detail = await _store.LoadDetail(summary.TopicId);
            if (detail == null) continue;

            var thumb = _store.PickThumbnail(summary.TopicId, detail.Images);
            if (thumb != null)
            {
                summary.ThumbnailPath = thumb;
                updated++;
            }
        }

        await _store.SaveIndex(index);
        return Ok(new { message = $"Backfilled {updated} thumbnails", updated });
    }

    // Starts a Playwright Chromium installation in the background; returns 409 if already running.
    [HttpPost("playwright/install")]
    public IActionResult StartPlaywrightInstall()
    {
        bool started = _playwright.StartInstall();
        if (!started)
            return Conflict(new { error = "Installation already in progress" });

        return Ok(new { message = "Installation started" });
    }

    // Returns live progress lines and completion state for an ongoing or finished Playwright install.
    [HttpGet("playwright/install-status")]
    public IActionResult GetPlaywrightInstallStatus()
    {
        var s = _playwright.GetInstallStatus();
        return Ok(new
        {
            running = s.Running,
            succeeded = s.Succeeded,
            exitCode = s.ExitCode,
            lines = s.Lines
        });
    }

    // Triggers an immediate re-download of the remote forum bundle, bypassing the configured TTL.
    // Used from the control panel when the user wants fresh data without waiting for the cooldown.
    [HttpPost("force-refresh-remote-data")]
    public async Task<IActionResult> ForceRefreshRemoteData()
    {
        await _forumFetch.EnsureDataFreshAsync(force: true);
        return Ok(new { success = true });
    }

    // Returns timestamps, mod/detail counts, disk size, source URL, and fetch interval
    // for the Remote Forum Data card in the control panel.
    [HttpGet("remote-data-info")]
    public async Task<IActionResult> GetRemoteDataInfo()
    {
        string dataPath = _config["ResolvedDataPath"]
            ?? Path.GetFullPath("../../data", AppContext.BaseDirectory);

        DateTime? updatedAt = null;
        DateTime? lastFetched = null;

        var metaPath = Path.Combine(dataPath, ForumDataFetchService.BundleMetaFileName);
        if (System.IO.File.Exists(metaPath))
        {
            try
            {
                var json = await System.IO.File.ReadAllTextAsync(metaPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("updatedAt", out var prop) &&
                    prop.TryGetDateTime(out var dt))
                    updatedAt = dt;
            }
            catch { /* non-fatal */ }
        }

        var fetchedPath = Path.Combine(dataPath, "remote-last-fetched.txt");
        if (System.IO.File.Exists(fetchedPath))
        {
            try
            {
                var raw = await System.IO.File.ReadAllTextAsync(fetchedPath);
                if (DateTime.TryParse(raw.Trim(), out var dt))
                    lastFetched = dt;
            }
            catch { /* non-fatal */ }
        }

        // Count detail.json files present on disk for the debug stats row.
        var modsDir = Path.Combine(dataPath, "mods");
        int detailCount = 0;
        if (Directory.Exists(modsDir))
            detailCount = Directory.GetFiles(modsDir, "detail.json", SearchOption.AllDirectories).Length;

        // Mod count and total data size come from the cached store stats (30 s cache, free after first call).
        var index = await _store.LoadIndex();
        var stats = await _store.GetStats();

        // Effective URL: scraper-config override wins; falls back to the app-config default.
        var scraperConfig = await _store.LoadConfig();
        var effectiveUrl = !string.IsNullOrWhiteSpace(scraperConfig.RemoteRawUrl)
            ? scraperConfig.RemoteRawUrl
            : _forumFetch.DefaultRemoteRawUrl;

        return Ok(new
        {
            updatedAt,
            lastFetched,
            modCount = index.Count,
            detailCount,
            dataSize = stats.TotalSizeFormatted,
            sourceUrl = effectiveUrl,
            fetchIntervalHours = _forumFetch.FetchIntervalHours,
            sourceOptions = _forumFetch.RemoteRawUrlOptions
        });
    }

    // Persists the user's remote source URL selection to scraper-config.json.
    // The URL must match one of the predefined RemoteRawUrlOptions entries.
    [HttpPut("remote-data-source")]
    public async Task<IActionResult> UpdateRemoteDataSource([FromBody] UpdateRemoteDataSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { error = "Url is required" });

        var options = _forumFetch.RemoteRawUrlOptions;
        if (options.Count > 0 && !options.Any(o => o.Url == request.Url))
            return BadRequest(new { error = "Url does not match any configured option" });

        // Persist via the existing scraper-config read/write path.
        var scraperConfig = await _store.LoadConfig();
        scraperConfig.RemoteRawUrl = request.Url;
        await _store.SaveConfig(scraperConfig);

        return Ok(new { sourceUrl = request.Url });
    }

    // Request body for the remote data source update endpoint.
    public class UpdateRemoteDataSourceRequest
    {
        public string Url { get; set; } = "";
    }

    // Deletes all locally cached remote forum data files so the next check re-downloads everything.
    // Removes mods-index.json, mods/ subtree, remote-bundle-meta.json, and the TTL timestamp file.
    [HttpDelete("remote-data-cache")]
    public IActionResult ClearRemoteDataCache()
    {
        string dataPath = _config["ResolvedDataPath"]
            ?? Path.GetFullPath("../../data", AppContext.BaseDirectory);

        int deleted = 0;

        // Single flat files written by the fetch service.
        foreach (var fileName in new[] { "mods-index.json", ForumDataFetchService.BundleMetaFileName, "remote-last-fetched.txt" })
        {
            var path = Path.Combine(dataPath, fileName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
                deleted++;
            }
        }

        // Per-topic detail files — delete the entire mods/ subtree.
        var modsDir = Path.Combine(dataPath, "mods");
        if (Directory.Exists(modsDir))
        {
            var detailFiles = Directory.GetFiles(modsDir, "detail.json", SearchOption.AllDirectories);
            foreach (var f in detailFiles)
            {
                System.IO.File.Delete(f);
                deleted++;
            }
        }

        return Ok(new { deleted });
    }

    // Logs a synthetic Warning and Error, each with a real attached exception, so the log UI exception
    // dropdown can be verified without needing a real failure to occur.
    [HttpPost("log-test-exception")]
    public IActionResult LogTestException()
    {
        try
        {
            throw new InvalidOperationException(
                "This is a test inner exception triggered from the control panel.",
                new ArgumentNullException("testParam", "Inner: simulated null argument"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Test exception logged at Warning level — verify the stack-trace dropdown in the log pane");
            Log.Error(ex, "Test exception logged at Error level — verify the stack-trace dropdown in the log pane");
        }

        return Ok(new { logged = true });
    }
}

