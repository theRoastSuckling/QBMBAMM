using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Services;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/scraper")]
// Exposes scraper status, start/stop, config, log, Playwright install, and remote-data-info endpoints for the UI control panel.
public class ScraperController : ControllerBase
{
    private readonly ScraperOrchestrator _orchestrator;
    private readonly JsonDataStore _store;
    private readonly IConfiguration _config;
    private readonly PlaywrightService _playwright;

    // Accepts PlaywrightService to support browser detection and install endpoints.
    public ScraperController(
        ScraperOrchestrator orchestrator,
        JsonDataStore store,
        IConfiguration config,
        PlaywrightService playwright)
    {
        _orchestrator = orchestrator;
        _store = store;
        _config = config;
        _playwright = playwright;
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

    // StartRequest carries scope, optional page count, topic IDs, and board selection flags.
    public class StartRequest
    {
        public string Scope { get; set; } = "all";
        public int? Pages { get; set; }
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
                scope.MaxPages = request.Pages ?? 1;
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

    // Returns whether Playwright Chromium is installed on this machine.
    [HttpGet("playwright-status")]
    public IActionResult GetPlaywrightStatus()
    {
        return Ok(new { isInstalled = _playwright.IsInstalled() });
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

    // Returns the UpdatedAt timestamp from the last successfully fetched remote bundle and
    // the last time this machine fetched it, so the UI can display data freshness.
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

        return Ok(new { updatedAt, lastFetched });
    }
}

