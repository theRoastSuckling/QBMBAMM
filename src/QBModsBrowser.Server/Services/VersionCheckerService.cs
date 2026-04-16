using System.Text.Json.Nodes;
using QBModsBrowser.Server.Models;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Fetches and caches remote version info for locally installed mods.
public class VersionCheckerService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    private readonly ILogger _log;
    private readonly LocalModService _localMods;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, VersionCheckResult> _cache = new();

    // Creates the service and configures the shared HTTP client.
    public VersionCheckerService(ILogger logger, LocalModService localMods)
    {
        _log = logger.ForContext<VersionCheckerService>();
        _localMods = localMods;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QBModsBrowser/1.0");
    }

    // Returns a cached version-check result for one mod, overlaying LocalVersion from the current
    // local scan so stale cached snapshots never show an outdated installed version.
    public VersionCheckResult? GetCachedResult(string modId)
    {
        if (_cache.TryGetValue(modId, out var result) && DateTime.UtcNow - result.CheckedAt < CacheTtl)
        {
            var currentLocal = _localMods.GetCachedMods()
                .FirstOrDefault(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase))
                ?.VersionChecker?.ModVersion;
            if (currentLocal != null)
            {
                result.LocalVersion = currentLocal;
                result.UpdateAvailable = currentLocal.CompareTo(result.RemoteVersion) < 0;
            }
            return result;
        }
        return null;
    }

    // Runs remote version checks for all local mods with valid version checker metadata.
    public async Task<Dictionary<string, VersionCheckResult>> CheckAllAsync(bool forceRefresh = false)
    {
        var mods = _localMods.GetCachedMods();
        return await CheckModsInternalAsync(mods, forceRefresh);
    }

    // Runs remote version checks for only the provided local mod ids after installs/refreshes.
    public async Task<Dictionary<string, VersionCheckResult>> CheckSpecificModsAsync(IReadOnlyCollection<string> modIds, bool forceRefresh = true)
    {
        if (modIds == null || modIds.Count == 0)
            return new Dictionary<string, VersionCheckResult>(_cache);

        var wanted = new HashSet<string>(
            modIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return new Dictionary<string, VersionCheckResult>(_cache);

        var mods = _localMods.GetCachedMods()
            .Where(m => wanted.Contains(m.ModId))
            .ToList();

        return await CheckModsInternalAsync(mods, forceRefresh);
    }

    // Executes bounded-concurrency remote checks and updates the in-memory cache.
    private async Task<Dictionary<string, VersionCheckResult>> CheckModsInternalAsync(IReadOnlyCollection<LocalMod> mods, bool forceRefresh)
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = new List<Task>();
            var semaphore = new SemaphoreSlim(5);

            foreach (var mod in mods)
            {
                if (mod.VersionChecker == null) continue;
                if (!SeemsLegit(mod.VersionChecker)) continue;

                if (!forceRefresh && _cache.TryGetValue(mod.ModId, out var cached) &&
                    DateTime.UtcNow - cached.CheckedAt < CacheTtl)
                    continue;

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var result = await CheckSingleModAsync(mod);
                        if (result != null)
                        {
                            lock (_cache) { _cache[mod.ModId] = result; }
                        }
                    }
                    finally { semaphore.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
            _log.Information("Version check complete: {Checked} mods checked, {Updates} updates available",
                _cache.Count, _cache.Values.Count(r => r.UpdateAvailable));
            return new Dictionary<string, VersionCheckResult>(_cache);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Fetches one mod's remote checker payload and computes update availability.
    private async Task<VersionCheckResult?> CheckSingleModAsync(LocalMod mod)
    {
        var vc = mod.VersionChecker;
        if (vc?.MasterVersionFile == null || vc.ModVersion == null)
            return null;

        try
        {
            var url = UrlNormalizer.NormalizeVersionFileUrl(vc.MasterVersionFile);
            var raw = await _http.GetStringAsync(url);
            var json = JsonFixHelper.FixJson(raw);
            var node = JsonNode.Parse(json);
            if (node == null) return null;

            VersionObject? remoteVersion = null;
            var versionNode = node["modVersion"];
            if (versionNode != null)
            {
                remoteVersion = new VersionObject
                {
                    Major = versionNode["major"]?.ToString(),
                    Minor = versionNode["minor"]?.ToString(),
                    Patch = versionNode["patch"]?.ToString()
                };
            }

            var directDownload = node["directDownloadURL"]?.GetValue<string>();

            bool updateAvailable = false;
            if (remoteVersion != null && vc.ModVersion != null)
            {
                updateAvailable = vc.ModVersion.CompareTo(remoteVersion) < 0;
            }

            return new VersionCheckResult
            {
                ModId = mod.ModId,
                LocalVersion = vc.ModVersion,
                RemoteVersion = remoteVersion,
                UpdateAvailable = updateAvailable,
                DirectDownloadUrl = directDownload,
                CheckedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Version check failed for {ModId}", mod.ModId);
            return new VersionCheckResult
            {
                ModId = mod.ModId,
                LocalVersion = vc.ModVersion,
                Error = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    // Verifies local checker metadata is sufficient before making remote requests.
    private static bool SeemsLegit(VersionCheckerInfo vc)
    {
        if (string.IsNullOrWhiteSpace(vc.MasterVersionFile)) return false;
        if (vc.ModVersion == null) return false;
        if (!Uri.TryCreate(vc.MasterVersionFile, UriKind.Absolute, out _)) return false;
        return true;
    }
}

// Cached outcome of a remote version check for one mod, including update availability and download URL.
public class VersionCheckResult
{
    public string ModId { get; set; } = "";
    public VersionObject? LocalVersion { get; set; }
    public VersionObject? RemoteVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? DirectDownloadUrl { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; }
}

