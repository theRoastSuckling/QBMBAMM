using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Utilities;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Coordinates queued downloads, archive installs, and history/state tracking for the UI.
public class DownloadManager
{
    private const int MaxConcurrent = 2;
    private const int MaxHistoryItems = 50;

    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;

    private readonly ILogger _log;
    private readonly Func<string> _getModsPath;
    private readonly ModInstallationService _installer;
    private readonly LocalModService _localMods;
    private readonly VersionCheckerService _versionChecker;
    private readonly AssumedDownloadService _assumedDownloads;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _concurrencyGate = new(MaxConcurrent, MaxConcurrent);
    private readonly object _listLock = new();
    private readonly List<DownloadItem> _items = [];
    private readonly string _topicArchiveMapPath;

    private Dictionary<int, List<TopicArchiveEntry>>? _topicArchiveMap;

    // Sets up HTTP/downloader dependencies and loads persisted topic-archive metadata.
    public DownloadManager(
        ILogger logger,
        Func<string> getModsPath,
        ModInstallationService installer,
        LocalModService localMods,
        VersionCheckerService versionChecker,
        AssumedDownloadService assumedDownloads,
        string dataPath)
    {
        _log = logger.ForContext<DownloadManager>();
        _getModsPath = getModsPath;
        _installer = installer;
        _localMods = localMods;
        _versionChecker = versionChecker;
        _assumedDownloads = assumedDownloads;
        _topicArchiveMapPath = Path.Combine(dataPath, "topic-archive-map.json");
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QBModsBrowser/1.0");
        LoadTopicArchiveMap();
    }

    // Returns persisted topic-to-archive mapping used to infer installed sub-mods.
    public Dictionary<int, List<TopicArchiveEntry>> GetTopicArchiveMap()
    {
        return _topicArchiveMap ?? new();
    }

    // Returns a snapshot of all download items for status polling endpoints.
    public List<DownloadItem> GetAll()
    {
        lock (_listLock) { return [.. _items]; }
    }

    // Looks up one download item by id for targeted status refresh.
    public DownloadItem? GetById(string id)
    {
        lock (_listLock) { return _items.FirstOrDefault(d => d.Id == id); }
    }

    // Extracts and installs archives already in the mods folder; inserts live DownloadItems so the poll endpoint shows per-archive progress.
    public async Task<ManualExtractResult> ExtractUnextractedArchivesAsync()
    {
        var modsPath = _getModsPath();
        var result = new ManualExtractResult();
        if (!Directory.Exists(modsPath))
            return result;

        var archivePaths = Directory
            .EnumerateFiles(modsPath, "*", SearchOption.TopDirectoryOnly)
            .Where(ArchiveFileHelper.HasSupportedArchiveExtension)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var installedByFolderName = new HashSet<string>(
            _localMods.GetCachedMods()
                .Select(m => m.FolderName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var installedByModId = new HashSet<string>(
            _localMods.GetCachedMods()
                .Select(m => m.ModId)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var archivePath in archivePaths)
        {
            var archiveName = Path.GetFileName(archivePath);
            var archiveBaseName = ArchiveFileHelper.GetArchiveBaseName(archivePath);
            var archiveModIds = await _installer.DetectArchiveModIdsAsync(archivePath);

            if (archiveModIds.Count > 0 && archiveModIds.All(id => installedByModId.Contains(id)))
            {
                result.Skipped++;
                result.SkippedArchives.Add(archiveName);
                AddManualExtractHistoryItem(archiveName, DownloadStatus.Canceled, archiveModIds, "Already installed (mod ID match)");
                continue;
            }

            if (installedByFolderName.Contains(archiveBaseName))
            {
                result.Skipped++;
                result.SkippedArchives.Add(archiveName);
                AddManualExtractHistoryItem(archiveName, DownloadStatus.Canceled, archiveModIds, "Already installed (folder match)");
                continue;
            }

            // Create a live item visible to polling before extraction starts.
            var liveItem = new DownloadItem
            {
                Url = "",
                ModName = ArchiveFileHelper.GetArchiveBaseName(archiveName),
                Status = DownloadStatus.Installing,
                FileName = archiveName,
                ArchiveFileName = archiveName
            };
            lock (_listLock)
            {
                _items.Insert(0, liveItem);
                TrimHistory();
            }

            try
            {
                var installResult = await InstallArchiveAndRefreshAsync(
                    archivePath,
                    modsPath,
                    (extracted, total) =>
                    {
                        liveItem.DownloadedBytes = extracted;
                        liveItem.TotalBytes = total;
                    });
                var installedFolderNames = installResult.InstalledFolderPaths
                    .Select(Path.GetFileName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                foreach (var folderName in installedFolderNames)
                    installedByFolderName.Add(folderName!);
                foreach (var modId in installResult.ModIds.Where(s => !string.IsNullOrWhiteSpace(s)))
                    installedByModId.Add(modId);

                // Update live item to reflect completion with resolved mod IDs.
                var completedIds = installResult.ModIds
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                liveItem.ModName = completedIds.Count > 0
                    ? string.Join(", ", completedIds)
                    : liveItem.ModName;
                liveItem.InstalledModIds = completedIds;
                liveItem.Status = DownloadStatus.Completed;
                liveItem.CompletedAt = DateTime.UtcNow;

                result.Extracted++;
                result.ExtractedArchives.Add(archiveName);
                result.InstalledModIds.AddRange(installResult.ModIds);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Manual extract failed for archive {Archive}", archiveName);
                liveItem.Status = DownloadStatus.Failed;
                liveItem.Error = ex.Message;
                liveItem.CompletedAt = DateTime.UtcNow;

                result.Failed++;
                result.FailedArchives.Add(archiveName);
            }
        }

        result.InstalledModIds = result.InstalledModIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    // Adds one synthetic downloads-history row for manual extract outcomes.
    private void AddManualExtractHistoryItem(string archiveName, DownloadStatus status, List<string>? modIds, string? error = null)
    {
        var detectedIds = modIds?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var displayName = detectedIds.Count > 0
            ? string.Join(", ", detectedIds)
            : ArchiveFileHelper.GetArchiveBaseName(archiveName);
        var item = new DownloadItem
        {
            Url = "",
            ModName = displayName,
            Status = status,
            FileName = archiveName,
            ArchiveFileName = archiveName,
            InstalledModIds = status == DownloadStatus.Completed ? detectedIds : [],
            Error = error,
            CompletedAt = DateTime.UtcNow
        };

        lock (_listLock)
        {
            _items.Insert(0, item);
            TrimHistory();
        }
    }

    // Queues a download job unless an equivalent in-flight download already exists.
    public DownloadItem Enqueue(string url, string modName, int? topicId = null,
        string? gameVersion = null, string? previousGameVersion = null,
        string? modVersion = null, string? previousModVersion = null)
    {
        var item = new DownloadItem
        {
            Url = url,
            ModName = modName,
            TopicId = topicId,
            GameVersion = gameVersion,
            PreviousGameVersion = previousGameVersion,
            ModVersion = modVersion,
            PreviousModVersion = previousModVersion
        };

        lock (_listLock)
        {
            var existing = _items.FirstOrDefault(d =>
                IsDownloadInFlight(d.Status)
                && string.Equals(d.Url, url, StringComparison.Ordinal));
            if (existing != null)
                return existing;

            _items.Insert(0, item);
            TrimHistory();
        }

        _ = Task.Run(() => ProcessDownloadAsync(item));
        _log.Information("Download enqueued: {ModName} from {Url}", modName, url);
        return item;
    }

    // Marks a queued or active job as canceled so workers stop at next safe point.
    public bool Cancel(string id)
    {
        lock (_listLock)
        {
            var item = _items.FirstOrDefault(d => d.Id == id);
            if (item == null) return false;
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.RetrievingInfo)
            {
                item.Status = DownloadStatus.Canceled;
                item.CompletedAt = DateTime.UtcNow;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// MediaFire <c>/file/.../file</c> pages return HTML; resolve to <c>download*.mediafire.com</c> CDN first.
    /// </summary>
    // Resolves host-specific page links into direct download URLs when possible.
    private async Task<string> ResolveEffectiveDownloadUrlAsync(string url, CancellationToken ct = default)
    {
        var normalized = UrlNormalizer.NormalizeDownloadUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return normalized;

        if (!uri.Host.Contains("mediafire.com", StringComparison.OrdinalIgnoreCase))
            return normalized;

        if (!uri.AbsolutePath.Contains("/file/", StringComparison.OrdinalIgnoreCase))
            return normalized;

        try
        {
            var resolved = await _assumedDownloads.ResolveSingleUrlAsync(url, ct);
            if (string.IsNullOrWhiteSpace(resolved))
                return normalized;

            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var r))
                return normalized;

            if (r.Host.StartsWith("download", StringComparison.OrdinalIgnoreCase)
                && r.Host.Contains("mediafire", StringComparison.OrdinalIgnoreCase))
            {
                _log.Information("Resolved MediaFire page URL to CDN");
                return resolved;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug(ex, "MediaFire URL resolution failed");
        }

        return normalized;
    }

    // Worker routine that downloads, installs, and finalizes one queued item.
    private async Task ProcessDownloadAsync(DownloadItem item)
    {
        await _concurrencyGate.WaitAsync();
        try
        {
            if (item.Status == DownloadStatus.Canceled) return;

            item.Status = DownloadStatus.RetrievingInfo;

            // Probe for file info with a 30-second hard timeout to avoid hanging indefinitely.
            string normalizedUrl;
            string? fileName = null;
            long totalBytes = 0;

            using (var retrieveInfoCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var ct = retrieveInfoCts.Token;
                try
                {
                    normalizedUrl = await ResolveEffectiveDownloadUrlAsync(item.Url, ct);

                    using var headReq = new HttpRequestMessage(HttpMethod.Head, normalizedUrl);
                    using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
                    headResp.EnsureSuccessStatusCode();

                    fileName = ExtractFileName(headResp.Content.Headers, normalizedUrl);
                    totalBytes = headResp.Content.Headers.ContentLength ?? 0;
                }
                catch (OperationCanceledException) when (retrieveInfoCts.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Failed;
                    item.Error = "Retrieving info timed out after 30 seconds";
                    item.CompletedAt = DateTime.UtcNow;
                    _log.Warning("RetrievingInfo timed out for {ModName} ({Url})", item.ModName, item.Url);
                    return;
                }
                catch
                {
                    // HEAD might fail — normalizedUrl may still be valid; proceed with GET.
                    normalizedUrl = UrlNormalizer.NormalizeDownloadUrl(item.Url);
                }
            }

            if (item.Status == DownloadStatus.Canceled) return;

            item.Status = DownloadStatus.Downloading;
            var modsPath = _getModsPath();
            var tempDir = Path.Combine(modsPath, ".qb-downloads");
            Directory.CreateDirectory(tempDir);

            var response = await _http.GetAsync(normalizedUrl, HttpCompletionOption.ResponseHeadersRead);

            // Handles Google Drive interstitials (including 400 pages) before enforcing success.
            if (IsGoogleDriveUrl(normalizedUrl) && (!response.IsSuccessStatusCode || IsHtmlResponse(response)))
            {
                response.Dispose();
                var confirmUrl = await ResolveGDriveVirusScanAsync(normalizedUrl);
                if (confirmUrl != null)
                {
                    response = await _http.GetAsync(confirmUrl, HttpCompletionOption.ResponseHeadersRead);
                }
                else
                {
                    item.Status = DownloadStatus.Failed;
                    item.Error = "Google Drive virus scan page could not be bypassed";
                    item.CompletedAt = DateTime.UtcNow;
                    return;
                }
            }

            response.EnsureSuccessStatusCode();

            if (string.IsNullOrEmpty(fileName))
                fileName = ExtractFileName(response.Content.Headers, normalizedUrl);
            if (string.IsNullOrEmpty(fileName))
                fileName = $"mod-{item.Id}.zip";

            item.FileName = fileName;
            item.TotalBytes = response.Content.Headers.ContentLength ?? totalBytes;

            var filePath = Path.Combine(tempDir, fileName);
            var partialPath = filePath + ".partial";

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                if (item.Status == DownloadStatus.Canceled)
                {
                    fileStream.Close();
                    TryDeleteFile(partialPath);
                    return;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                item.DownloadedBytes = totalRead;
            }

            fileStream.Close();

            // Rename partial to final
            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(partialPath, filePath);

            // Install
            item.Status = DownloadStatus.Installing;
            item.ArchiveFileName = fileName;
            item.DownloadedBytes = 0;
            item.TotalBytes = 0;
            try
            {
                var installResult = await InstallArchiveAndRefreshAsync(
                    filePath,
                    modsPath,
                    (extracted, total) =>
                    {
                        item.DownloadedBytes = extracted;
                        item.TotalBytes = total;
                    });
                var modIds = installResult.ModIds;
                item.InstalledModIds = [.. modIds];
                item.Status = DownloadStatus.Completed;
                item.DownloadedBytes = item.TotalBytes > 0 ? item.TotalBytes : item.DownloadedBytes;
                item.CompletedAt = DateTime.UtcNow;
                _log.Information("Download and install complete: {ModName} -> {ModIds}", item.ModName, modIds);

                if (item.TopicId.HasValue && modIds.Count > 0)
                    UpdateTopicArchiveMap(item.TopicId.Value, fileName ?? "", item.Url, modIds);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                item.Error = $"Install failed: {ex.Message}";
                item.CompletedAt = DateTime.UtcNow;
                _log.Error(ex, "Install failed for {ModName}", item.ModName);
            }
            finally
            {
                TryDeleteFile(filePath);
            }
        }
        catch (Exception ex)
        {
            if (item.Status != DownloadStatus.Canceled)
            {
                item.Status = DownloadStatus.Failed;
                item.Error = ex.Message;
                item.CompletedAt = DateTime.UtcNow;
                _log.Error(ex, "Download failed for {ModName}", item.ModName);
            }
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    // Identifies statuses that mean a download is still active or pending.
    private static bool IsDownloadInFlight(DownloadStatus s) =>
        s is DownloadStatus.Queued or DownloadStatus.RetrievingInfo or DownloadStatus.Downloading or DownloadStatus.Installing;

    // Detects Google Drive hosts to enable confirm-page fallback logic.
    private static bool IsGoogleDriveUrl(string url) =>
        url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);

    // Checks response content type to detect HTML gate pages vs binary downloads.
    private static bool IsHtmlResponse(HttpResponseMessage response)
    {
        var ct = response.Content.Headers.ContentType?.MediaType;
        return ct != null && ct.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    // Extracts/constructs a confirm URL to bypass Google Drive virus-scan interstitials.
    private async Task<string?> ResolveGDriveVirusScanAsync(string originalUrl)
    {
        try
        {
            using var response = await _http.GetAsync(originalUrl, HttpCompletionOption.ResponseContentRead);
            var html = await response.Content.ReadAsStringAsync();

            // Extracts Google Drive interstitial form action and hidden fields into a working URL.
            var formActionMatch = Regex.Match(html, @"action=""([^""]+)""", RegexOptions.IgnoreCase);
            if (formActionMatch.Success)
            {
                var actionUrl = formActionMatch.Groups[1].Value
                    .Replace("&amp;", "&");
                if (!actionUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    actionUrl = "https://drive.usercontent.google.com" + actionUrl;

                var hiddenMatches = Regex.Matches(
                    html,
                    @"<input[^>]*type=""hidden""[^>]*name=""([^""]+)""[^>]*value=""([^""]*)""[^>]*>",
                    RegexOptions.IgnoreCase);

                if (hiddenMatches.Count == 0)
                    return actionUrl;

                var query = string.Join("&", hiddenMatches
                    .Select(m =>
                        $"{Uri.EscapeDataString(m.Groups[1].Value)}={Uri.EscapeDataString(m.Groups[2].Value)}"));

                if (string.IsNullOrWhiteSpace(query))
                    return actionUrl;

                return actionUrl + (actionUrl.Contains('?') ? "&" : "?") + query;
            }

            // Fallback: try adding confirm=t to original URL
            if (!originalUrl.Contains("confirm=t", StringComparison.OrdinalIgnoreCase))
            {
                var sep = originalUrl.Contains('?') ? "&" : "?";
                return originalUrl + sep + "confirm=t";
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to resolve Google Drive virus scan page");
            return null;
        }
    }

    // Determines a file name from headers first, then URL path fallback.
    private static string? ExtractFileName(HttpContentHeaders? headers, string url)
    {
        if (headers?.ContentDisposition?.FileName is string cdName)
        {
            var name = cdName.Trim('"', ' ');
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        if (headers?.ContentDisposition?.FileNameStar is string cdNameStar)
        {
            var name = cdNameStar.Trim('"', ' ');
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        // Fall back to last path segment
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment.Contains('.'))
                return Uri.UnescapeDataString(lastSegment);
        }

        return null;
    }

    // Best-effort cleanup helper for temporary download files.
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // Installs one archive, refreshes local cache, and warms remote version-check cache.
    private async Task<ModArchiveInstallResult> InstallArchiveAndRefreshAsync(
        string archivePath,
        string modsPath,
        Action<long, long>? onExtractProgress = null)
    {
        var installResult = await _installer.InstallFromArchiveAsync(archivePath, modsPath, onExtractProgress);
        var modIds = installResult.ModIds;
        await AddInstalledModsToEnabledModsJsonAsync(modsPath, modIds);
        if (installResult.InstalledFolderPaths.Count > 0)
        {
            try
            {
                await _localMods.RefreshModsAtPathsAsync(installResult.InstalledFolderPaths);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Local mod cache refresh after archive install failed");
            }
        }

        if (modIds.Count > 0)
        {
            try
            {
                await _versionChecker.CheckSpecificModsAsync(modIds, forceRefresh: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Post-install version check refresh failed for {ModIds}", modIds);
            }
        }

        return installResult;
    }

    // Ensures newly installed mods are added to enabled_mods.json automatically.
    private async Task AddInstalledModsToEnabledModsJsonAsync(string modsPath, List<string> modIds)
    {
        if (modIds == null || modIds.Count == 0) return;

        var enabledModsPath = Path.Combine(modsPath, "enabled_mods.json");
        try
        {
            DownloadEnabledModsFile data;
            if (File.Exists(enabledModsPath))
            {
                var json = await File.ReadAllTextAsync(enabledModsPath);
                data = JsonSerializer.Deserialize<DownloadEnabledModsFile>(json, JsonOpts) ?? new DownloadEnabledModsFile();
            }
            else
            {
                data = new DownloadEnabledModsFile();
            }

            var changed = false;
            foreach (var modId in modIds)
            {
                if (string.IsNullOrWhiteSpace(modId)) continue;
                if (data.EnabledMods.Contains(modId, StringComparer.OrdinalIgnoreCase)) continue;
                data.EnabledMods.Add(modId);
                changed = true;
            }

            if (!changed) return;

            var output = JsonSerializer.Serialize(data, JsonOpts);
            await File.WriteAllTextAsync(enabledModsPath, output);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to update enabled_mods.json after install");
        }
    }

    // Keeps download history bounded by removing oldest finished entries.
    private void TrimHistory()
    {
        while (_items.Count > MaxHistoryItems)
        {
            var oldest = _items.FindLastIndex(d =>
                d.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled);
            if (oldest >= 0)
                _items.RemoveAt(oldest);
            else
                break;
        }
    }

    // Saves archive-to-mod-id hints for topics to improve future matching.
    private void UpdateTopicArchiveMap(int topicId, string archiveName, string downloadUrl, List<string> modIds)
    {
        _topicArchiveMap ??= new();
        if (!_topicArchiveMap.TryGetValue(topicId, out var entries))
        {
            entries = [];
            _topicArchiveMap[topicId] = entries;
        }

        entries.RemoveAll(e => e.ArchiveName == archiveName || e.DownloadUrl == downloadUrl);
        entries.Add(new TopicArchiveEntry
        {
            ArchiveName = archiveName,
            DownloadUrl = downloadUrl,
            ModIds = modIds
        });

        _ = PersistTopicArchiveMapAsync();
    }

    // Loads topic archive mapping from disk on service startup.
    private void LoadTopicArchiveMap()
    {
        try
        {
            if (!File.Exists(_topicArchiveMapPath))
            {
                _topicArchiveMap = new();
                return;
            }
            var json = File.ReadAllText(_topicArchiveMapPath);
            _topicArchiveMap = JsonSerializer.Deserialize<Dictionary<int, List<TopicArchiveEntry>>>(json, JsonOpts) ?? new();
            _log.Information("Loaded topic-archive map with {Count} topics", _topicArchiveMap.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load topic-archive map");
            _topicArchiveMap = new();
        }
    }

    // Persists updated topic archive mapping for cross-session reuse.
    private async Task PersistTopicArchiveMapAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_topicArchiveMap ?? new(), JsonOpts);
            await File.WriteAllTextAsync(_topicArchiveMapPath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to persist topic-archive map");
        }
    }
}

// Stores one downloaded archive reference and the mod ids found in it.
public class TopicArchiveEntry
{
    public string ArchiveName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public List<string> ModIds { get; set; } = [];
}

// DTO for reading/writing enabled_mods.json content.
public class DownloadEnabledModsFile
{
    public List<string> EnabledMods { get; set; } = [];
}

// Summarizes results of extracting archives already present in the mods folder.
public class ManualExtractResult
{
    public int Extracted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> ExtractedArchives { get; set; } = [];
    public List<string> SkippedArchives { get; set; } = [];
    public List<string> FailedArchives { get; set; } = [];
    public List<string> InstalledModIds { get; set; } = [];
}
