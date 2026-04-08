using System.Text.Json;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Keeps client machines up-to-date by fetching the remote forum data bundle on startup and page load.
// No background polling — checks only when the app is actively used.
public class ForumDataFetchService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string LastFetchedFileName = "remote-last-fetched.txt";
    // Stores the UpdatedAt timestamp from the most recently applied remote bundle.
    public const string BundleMetaFileName = "remote-bundle-meta.json";

    private readonly ILogger _log;
    private readonly ForumDataRepoConfig _config;
    private readonly string _dataPath;
    private readonly JsonDataStore _store;
    private readonly ForumDataBundler _bundler;
    private readonly AssumedDownloadService _assumed;
    private readonly HttpClient _http;

    // Guards against concurrent fetch attempts triggered by rapid page loads.
    private int _isFetching;

    // Accepts data-path and services needed to unpack a fetched bundle into the local data folder.
    public ForumDataFetchService(
        ILogger logger,
        ForumDataRepoConfig config,
        string dataPath,
        JsonDataStore store,
        ForumDataBundler bundler,
        AssumedDownloadService assumed)
    {
        _log = logger.ForContext<ForumDataFetchService>();
        _config = config;
        _dataPath = dataPath;
        _store = store;
        _bundler = bundler;
        _assumed = assumed;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QBModsBrowser/1.0");
    }

    // Runs the freshness check once on app startup; subsequent checks are triggered by page loads.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("ForumDataFetchService started");
        await EnsureDataFreshAsync(stoppingToken);
    }

    // Fetches and unpacks the remote bundle if the local data has exceeded the configured TTL.
    // Skips entirely when LocalRepoPath is set (operator machine — data is always fresh from scrapes).
    // Safe to call concurrently: only one fetch runs at a time.
    public async Task EnsureDataFreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _isFetching, 1, 0) != 0)
        {
            _log.Debug("Forum data fetch already in progress, skipping concurrent call");
            return;
        }

        try
        {
            await FetchInternalAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _isFetching, 0);
        }
    }

    // Inner fetch logic, runs only when no other fetch is active.
    private async Task FetchInternalAsync(CancellationToken ct)
    {
        var scraperConfig = await _store.LoadConfig();
        if (scraperConfig.DisableRemoteForumDataFetch)
        {
            _log.Debug("Remote forum data fetch skipped: disabled by user setting");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.RemoteRawUrl))
        {
            _log.Warning("Remote forum data fetch skipped: RemoteRawUrl is not configured");
            return;
        }

        var lastFetchedPath = Path.Combine(_dataPath, LastFetchedFileName);
        var fetchInterval = TimeSpan.FromHours(_config.FetchIntervalHours > 0 ? _config.FetchIntervalHours : 6);

        if (File.Exists(lastFetchedPath))
        {
            var raw = await File.ReadAllTextAsync(lastFetchedPath, ct);
            if (DateTime.TryParse(raw.Trim(), out var lastFetched))
            {
                var age = DateTime.UtcNow - lastFetched;
                if (age < fetchInterval)
                {
                    _log.Debug(
                        "Remote forum data is fresh (age={Age:hh\\:mm}, interval={Interval:hh\\:mm}), skipping fetch",
                        age, fetchInterval);
                    return;
                }
            }
        }

        _log.Information("Fetching remote forum data bundle from {Url}", _config.RemoteRawUrl);

        ForumDataBundle? bundle;
        try
        {
            var json = await _http.GetStringAsync(_config.RemoteRawUrl, ct);
            bundle = JsonSerializer.Deserialize<ForumDataBundle>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to fetch remote forum data bundle");
            return;
        }

        if (bundle == null)
        {
            _log.Warning("Remote forum data bundle was empty or could not be deserialized");
            return;
        }

        try
        {
            await _bundler.UnpackBundleAsync(bundle, _store, _assumed);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to unpack remote forum data bundle");
            return;
        }

        // Record fetch time only after a successful unpack so a failed attempt retries next cycle.
        await File.WriteAllTextAsync(lastFetchedPath, DateTime.UtcNow.ToString("O"), ct);

        // Persist the bundle's own UpdatedAt so the UI can show when the forum data was last scraped.
        var metaPath = Path.Combine(_dataPath, BundleMetaFileName);
        await File.WriteAllTextAsync(metaPath,
            System.Text.Json.JsonSerializer.Serialize(new { updatedAt = bundle.UpdatedAt }), ct);

        _log.Information(
            "Remote forum data applied: {ModCount} mods, bundleUpdatedAt={UpdatedAt:u}",
            bundle.Index.Count, bundle.UpdatedAt);
    }
}
