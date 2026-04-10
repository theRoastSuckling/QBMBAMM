using Microsoft.Playwright;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Services;
using QBModsBrowser.Scraper.Storage;
using Serilog;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Manages scrape lifecycle and runs post-scrape assumed-download resolution for all mods.
public class ScraperOrchestrator : BackgroundService
{
    private readonly ILogger _log;
    private readonly JsonDataStore _store;
    private readonly AssumedDownloadService _assumed;
    private readonly ForumDataBundler _bundler;
    private readonly ForumDataPublisher _publisher;
    private readonly PlaywrightService _playwright;
    private readonly object _lock = new();

    // Name of the metadata file that the UI reads to display the "Last scraped" timestamp.
    private const string BundleMetaFileName = ForumDataFetchService.BundleMetaFileName;

    private CancellationTokenSource? _scrapeCts;
    private ScraperEngine? _currentEngine;
    private ScrapeJob _lastJob = new();
    private ScrapeResult? _lastResult;

    // Accepts bundler, publisher, and playwright so a missing-browser scrape failure triggers the install prompt.
    public ScraperOrchestrator(
        JsonDataStore store,
        AssumedDownloadService assumed,
        ForumDataBundler bundler,
        ForumDataPublisher publisher,
        PlaywrightService playwright)
    {
        _log = Log.Logger.ForContext<ScraperOrchestrator>();
        _store = store;
        _assumed = assumed;
        _bundler = bundler;
        _publisher = publisher;
        _playwright = playwright;
    }

    public ScrapeJob CurrentJob
    {
        get
        {
            lock (_lock)
                return _currentEngine?.CurrentJob ?? _lastJob;
        }
    }

    public ScrapeResult? LastResult
    {
        get
        {
            lock (_lock)
                return _lastResult;
        }
    }

    public bool IsScraping
    {
        get
        {
            lock (_lock)
                return _currentEngine != null;
        }
    }

    public async Task<bool> StartScrape(ScrapeScope scope)
    {
        lock (_lock)
        {
            if (_currentEngine != null)
                return false;
        }

        _scrapeCts = new CancellationTokenSource();
        var config = await _store.LoadConfig();

        var engine = new ScraperEngine(
            _log, _store, _store.BasePath,
            config.DelayBetweenPagesMs, config.DelayBetweenTopicsMs);

        lock (_lock)
            _currentEngine = engine;

        _ = Task.Run(async () =>
        {
            try
            {
                // Resolve assumed downloads for each topic immediately after its detail is saved,
                // running concurrently with the next topic scrape so there is no added latency.
                var result = await engine.Run(scope, _scrapeCts.Token, async detail =>
                {
                    try
                    {
                        var links = SpoilerLinkFilter.FilterOutSpoilerLinks(detail.ContentHtml, detail.Links);
                        await _assumed.ResolveAsync(detail.TopicId, links);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Post-topic resolution failed for {TopicId}", detail.TopicId);
                    }
                });
                lock (_lock)
                {
                    _lastResult = result;
                    _lastJob = engine.CurrentJob;
                    _currentEngine = null;
                }

                // After any successful scrape, refresh the bundle-meta file so the UI "Last scraped"
                // label reflects the freshest local data rather than the last remote-download time.
                if (result.Success)
                    _ = Task.Run(() => UpdateBundleMetaAsync());

                // Bundle and publish only when the scrape succeeded and opted in; fire-and-forget so it never blocks the scrape loop.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Skip publish entirely if the scrape was cancelled, force-closed, or failed.
                        if (!result.Success)
                        {
                            _log.Information("Skipping post-scrape publish — scrape did not succeed (cancelled or failed).");
                            return;
                        }

                        var postConfig = await _store.LoadConfig();
                        if (!postConfig.AutoPublishAfterScrape)
                            return;

                        var bundle = await _bundler.CreateBundleAsync(_store, _assumed);
                        await _publisher.PublishAsync(bundle);
                    }
                    catch (Exception pubEx)
                    {
                        _log.Warning(pubEx, "Post-scrape forum data publish failed");
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Scrape task crashed");
                // Playwright throws when the required Chromium revision wasn't downloaded yet
                // (e.g. after an app update that bumped the Playwright version). Mark the browser
                // as missing so the control panel switches back to the install prompt immediately.
                if (ex is PlaywrightException && ex.Message.Contains("Executable doesn't exist"))
                    _playwright.MarkBrowserMissing();
                lock (_lock)
                {
                    _lastJob = engine.CurrentJob;
                    _currentEngine = null;
                }
            }
            finally
            {
                _scrapeCts?.Dispose();
                _scrapeCts = null;
            }
        });

        return true;
    }

    public void StopScrape()
    {
        _scrapeCts?.Cancel();
    }

    // Writes the max ScrapedAt from the local index into remote-bundle-meta.json so the UI
    // "Last scraped" label stays current after a local scrape, not just after a remote fetch.
    private async Task UpdateBundleMetaAsync()
    {
        try
        {
            var index = await _store.LoadIndex();
            if (index.Count == 0) return;

            var updatedAt = index.Max(s => s.ScrapedAt);
            var metaPath = Path.Combine(_store.BasePath, BundleMetaFileName);
            await File.WriteAllTextAsync(metaPath,
                System.Text.Json.JsonSerializer.Serialize(new { updatedAt }));

            _log.Debug("Bundle meta updated after local scrape: updatedAt={UpdatedAt:u}", updatedAt);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to update bundle meta after local scrape");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("ScraperOrchestrator background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = await _store.LoadConfig();
                if (config.AutoScrapeIntervalHours > 0 && !IsScraping)
                {
                    var lastJob = CurrentJob;
                    var nextRun = (lastJob.FinishedAt ?? DateTime.MinValue)
                        .AddHours(config.AutoScrapeIntervalHours);

                    if (DateTime.UtcNow >= nextRun)
                    {
                        _log.Information("Auto-scrape triggered (interval: {Hours}h)", config.AutoScrapeIntervalHours);
                        await StartScrape(new ScrapeScope { Type = ScopeType.NewData });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error in auto-scrape check");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

