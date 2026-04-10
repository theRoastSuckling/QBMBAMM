using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Utilities;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Resolves likely direct download links from forum post URLs across common hosting providers.
public partial class AssumedDownloadService
{
    // No expiry: link data only changes when a new scrape runs; fingerprint check in ResolveAsync handles changed links.
    private static readonly TimeSpan CacheTtl = TimeSpan.MaxValue;

    /// <summary>Bump when assumed-download resolution changes so disk cache is not reused with stale data.</summary>
    private const int AssumedDownloadCacheSchema = 8;

    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;

    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<int, CachedResult> _cache = new();
    private readonly SemaphoreSlim _resolveLock = new(3, 3);

    // Creates resolver HTTP client and loads persisted resolution cache.
    public AssumedDownloadService(ILogger logger, string dataPath)
    {
        _log = logger.ForContext<AssumedDownloadService>();
        _cachePath = Path.Combine(dataPath, "assumed-downloads-cache.json");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // MediaFire and some hosts omit CDN links when UA looks like a bot.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        LoadCacheFromDisk();
    }

    // Resolves and classifies downloadable candidates for a topic link set with caching.
    public async Task<List<AssumedDownloadCandidate>> ResolveAsync(int topicId, List<LinkRef> links)
    {
        var linkFingerprint = FingerprintLinks(links);
        if (_cache.TryGetValue(topicId, out var cached)
            && cached.Schema >= AssumedDownloadCacheSchema
            && !string.IsNullOrEmpty(cached.LinkFingerprint)
            && string.Equals(cached.LinkFingerprint, linkFingerprint, StringComparison.Ordinal)
            && DateTime.UtcNow - cached.ResolvedAt < CacheTtl)
            return cached.Candidates;

        var candidates = new List<AssumedDownloadCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;
            var rawUrl = System.Net.WebUtility.HtmlDecode(link.Url.Trim());
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;

            var host = uri.Host.ToLowerInvariant();
            var url = rawUrl;

            var normalizedKey = NormalizeUrlForDedup(url);
            if (!seen.Add(normalizedKey)) continue;

            try
            {
                var candidate = await ClassifyAndResolve(url, host, link.Text);
                if (candidate != null)
                    candidates.Add(candidate);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Failed to resolve assumed download link: {Url}", url);
            }
        }

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.FileName))
                c.FileName = ExtractArchiveFileNameHint(c.ResolvedDirectUrl ?? c.OriginalUrl, c.LinkText);
        }

        // Heuristic: "alternate download" links without filenames are often mirrors of the same archive.
        var knownNames = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.FileName))
            .Select(c => c.FileName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (knownNames.Count == 1)
        {
            foreach (var c in candidates.Where(c =>
                string.IsNullOrWhiteSpace(c.FileName)
                && !string.IsNullOrWhiteSpace(c.LinkText)
                && c.LinkText.Contains("alternate", StringComparison.OrdinalIgnoreCase)))
            {
                c.FileName = knownNames[0];
            }

            var only = knownNames[0];
            foreach (var c in candidates.Where(c =>
                string.IsNullOrWhiteSpace(c.FileName)
                && string.Equals(c.SourceHost, "Google Drive", StringComparison.OrdinalIgnoreCase)
                && candidates.Exists(o => !ReferenceEquals(o, c)
                    && string.Equals(o.FileName?.Trim(), only, StringComparison.OrdinalIgnoreCase))))
            {
                c.FileName = only;
            }
        }

        // Post-resolution deduplication: remove candidates with same resolved URL
        var resolvedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        candidates = candidates.Where(c =>
        {
            var key = NormalizeUrlForDedup(c.ResolvedDirectUrl ?? c.OriginalUrl);
            return resolvedSeen.Add(key);
        }).ToList();

        // Cross-host dedup by archive filename (e.g. GitHub + Google Drive for same zip)
        var fileSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        candidates = candidates.Where(c =>
        {
            if (string.IsNullOrWhiteSpace(c.FileName)) return true;
            return fileSeen.Add(c.FileName.Trim());
        }).ToList();

        _cache[topicId] = new CachedResult
        {
            Candidates = candidates,
            ResolvedAt = DateTime.UtcNow,
            Schema = AssumedDownloadCacheSchema,
            LinkFingerprint = linkFingerprint
        };
        _ = PersistCacheAsync();

        return candidates;
    }

    // Resolves one URL to its direct-download equivalent when provider rules allow.
    // Resolves a single URL to its direct download target; respects an optional cancellation token for timeouts.
    public async Task<string?> ResolveSingleUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = System.Net.WebUtility.HtmlDecode(url.Trim());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();

        await _resolveLock.WaitAsync(ct);
        try
        {
            var candidate = await ClassifyAndResolve(url, host, null);
            return candidate?.ResolvedDirectUrl ?? candidate?.OriginalUrl;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    // Returns valid cached candidates for a topic when cache fingerprint still matches.
    public List<AssumedDownloadCandidate>? GetCachedCandidates(int topicId)
    {
        if (_cache.TryGetValue(topicId, out var cached)
            && cached.Schema >= AssumedDownloadCacheSchema
            && !string.IsNullOrEmpty(cached.LinkFingerprint)
            && DateTime.UtcNow - cached.ResolvedAt < CacheTtl)
            return cached.Candidates;
        return null;
    }

    // Checks whether usable cached candidate results exist for the topic.
    public bool HasCachedCandidates(int topicId)
    {
        return _cache.TryGetValue(topicId, out var cached)
            && cached.Schema >= AssumedDownloadCacheSchema
            && !string.IsNullOrEmpty(cached.LinkFingerprint)
            && DateTime.UtcNow - cached.ResolvedAt < CacheTtl
            && cached.Candidates.Count > 0;
    }

    // Returns all cached candidates keyed by topic id, used when bundling data for export.
    public Dictionary<int, List<AssumedDownloadCandidate>> GetAllCandidates()
    {
        return _cache
            .Where(kvp => kvp.Value.Candidates.Count > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Candidates);
    }

    // Populates the in-memory cache from an imported bundle, using an empty fingerprint so that
    // a real ResolveAsync call for the same topic will still overwrite with freshly resolved data.
    public void ImportCandidates(Dictionary<int, List<AssumedDownloadCandidate>> data)
    {
        foreach (var (topicId, candidates) in data)
        {
            _cache[topicId] = new CachedResult
            {
                Candidates = candidates,
                ResolvedAt = DateTime.UtcNow,
                Schema = AssumedDownloadCacheSchema,
                LinkFingerprint = ""
            };
        }
        _log.Debug("Imported {Count} assumed-download entries from bundle", data.Count);
    }

    /// <summary>Stable fingerprint of input links so cache invalidates when spoiler filtering or link list changes.</summary>
    // Builds deterministic fingerprint so cache invalidates when link sets change.
    private static string FingerprintLinks(IReadOnlyList<LinkRef> links)
    {
        var parts = new List<string>();
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;
            var raw = WebUtility.HtmlDecode(link.Url.Trim());
            if (!Uri.TryCreate(raw, UriKind.Absolute, out _)) continue;
            parts.Add(NormalizeUrlForDedup(raw));
        }

        parts.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('\u001e', parts);
    }

    /// <summary>Quick heuristic: checks if any link points to a known download host. No HTTP calls.</summary>
    // Fast precheck to skip expensive resolution when no known download hosts exist.
    public static bool HasPotentialDownloadLinks(List<LinkRef>? links)
    {
        if (links == null || links.Count == 0) return false;
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) continue;
            var h = uri.Host.ToLowerInvariant();
            if (IsDownloadHost(h, uri.AbsolutePath))
                return true;
        }
        return false;
    }

    // Identifies hosts/paths that are commonly used for mod archive downloads.
    private static bool IsDownloadHost(string host, string path)
    {
        if (host.Contains("github.com"))
        {
            if (path.Contains("/releases", StringComparison.OrdinalIgnoreCase) || path.Contains("/download/", StringComparison.OrdinalIgnoreCase))
                return true;
            if ((path.Contains("/raw/", StringComparison.OrdinalIgnoreCase) || path.Contains("/archive/", StringComparison.OrdinalIgnoreCase))
                && ArchiveFileHelper.HasSupportedArchiveExtension(path))
                return true;
        }
        if (host.Contains("drive.google.com") || host.Contains("drive.usercontent.google.com"))
            return true;
        if (host.Contains("dropbox.com"))
            return true;
        if (host.Contains("mediafire.com"))
            return true;
        if (host.Contains("onedrive.live.com") || host.Contains("1drv.ms"))
            return true;
        if (host.Contains("bitbucket.org") && path.Contains("/downloads/"))
            return true;
        if (host.Contains("patreon.com"))
            return true;
        if (UrlShortenerHosts.Contains(host))
            return true;
        return false;
    }

    private static readonly HashSet<string> NonArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".mp3", ".wav", ".flac", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".pdf", ".txt", ".html", ".htm", ".jar", ".exe", ".doc", ".docx", ".csv", ".xml"
    };

    // Filters obvious non-archive files so resolver avoids false positives.
    private static bool IsNonArchiveFile(string? fileName, string? linkText)
    {
        var name = fileName ?? linkText ?? "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lower = name.ToLowerInvariant().TrimEnd('/');
        foreach (var ext in NonArchiveExtensions)
        {
            if (lower.EndsWith(ext)) return true;
        }
        return false;
    }

    // Extracts likely archive filenames from URL path or link text hints.
    private static string? ExtractArchiveFileNameHint(string? url, string? linkText)
    {
        static string? FromText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"([A-Za-z0-9._\- ]+\.(?:zip|rar|7z|tar\.gz|tar|bz2))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value.Trim();
        }

        var fromLinkText = FromText(linkText);
        if (!string.IsNullOrWhiteSpace(fromLinkText))
            return fromLinkText;

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = Uri.UnescapeDataString((uri.Segments.LastOrDefault() ?? "").Trim('/'));
            var fromPath = FromText(last);
            if (!string.IsNullOrWhiteSpace(fromPath))
                return fromPath;
        }

        return null;
    }

    // Normalizes URL for deduplication across query variants and case differences.
    private static string NormalizeUrlForDedup(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var normalized = uri.GetLeftPart(UriPartial.Path).ToLowerInvariant();
            return normalized;
        }
        return url.ToLowerInvariant();
    }

    private static readonly HashSet<string> UrlShortenerHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyurl.com", "bit.ly", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly"
    };

    // Applies host-specific rules to classify and resolve a potential download URL.
    private async Task<AssumedDownloadCandidate?> ClassifyAndResolve(string url, string host, string? linkText)
    {
        // Follow URL shortener redirects to get the real destination
        if (UrlShortenerHosts.Contains(host))
        {
            await _resolveLock.WaitAsync();
            try
            {
                using var shortHandler = new HttpClientHandler { AllowAutoRedirect = false };
                using var shortClient = new HttpClient(shortHandler) { Timeout = TimeSpan.FromSeconds(10) };
                shortClient.DefaultRequestHeaders.UserAgent.ParseAdd("QBModsBrowser/1.0");
                using var shortResp = await shortClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                var location = shortResp.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location) && Uri.TryCreate(location, UriKind.Absolute, out var resolvedUri))
                {
                    _log.Debug("URL shortener {Url} resolved to {Resolved}", url, location);
                    url = location;
                    host = resolvedUri.Host.ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Failed to follow URL shortener {Url}", url);
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        // GitHub direct asset download
        if (host.Contains("github.com") && GitHubDirectAssetRegex().IsMatch(url))
        {
            var fileName = Uri.UnescapeDataString(url.Split('/').LastOrDefault()?.Split('?').FirstOrDefault() ?? "");
            if (IsNonArchiveFile(fileName, linkText)) return null;
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                ResolvedDirectUrl = url,
                SourceHost = "GitHub",
                FileName = fileName,
                Confidence = "high",
                LinkText = linkText
            };
        }

        // GitHub releases page (tagged or latest or bare /releases)
        if (host.Contains("github.com") && GitHubReleasesRegex().IsMatch(url))
        {
            return await ResolveGitHubReleasesAsync(url, linkText);
        }

        // Bitbucket direct downloads
        if (host.Contains("bitbucket.org") && url.Contains("/downloads/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Uri.UnescapeDataString(url.Split('/').LastOrDefault()?.Split('?').FirstOrDefault() ?? "");
            if (IsNonArchiveFile(fileName, linkText)) return null;
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                ResolvedDirectUrl = url,
                SourceHost = "Bitbucket",
                FileName = fileName,
                Confidence = "high",
                LinkText = linkText
            };
        }

        // Google Drive
        if (host.Contains("drive.google.com") || host.Contains("drive.usercontent.google.com"))
        {
            if (IsNonArchiveFile(null, linkText)) return null;
            var resolved = UrlNormalizer.NormalizeDownloadUrl(url);
            var fileName = ExtractArchiveFileNameHint(url, linkText);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = await TryResolveGoogleDriveFileNameAsync(url, resolved);
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                ResolvedDirectUrl = resolved,
                SourceHost = "Google Drive",
                FileName = fileName,
                Confidence = "medium",
                LinkText = linkText
            };
        }

        // Dropbox
        if (host.Contains("dropbox.com"))
        {
            var fileName = url.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
            if (IsNonArchiveFile(fileName, linkText)) return null;
            var resolved = UrlNormalizer.NormalizeDownloadUrl(url);
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                ResolvedDirectUrl = resolved,
                SourceHost = "Dropbox",
                FileName = fileName,
                Confidence = "medium",
                LinkText = linkText
            };
        }

        // MediaFire
        if (host.Contains("mediafire.com"))
        {
            var fileName = url.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
            if (IsNonArchiveFile(fileName, linkText)) return null;
            return await ResolveMediaFireAsync(url, linkText);
        }

        // OneDrive
        if (host.Contains("onedrive.live.com") || host.Contains("1drv.ms"))
        {
            if (IsNonArchiveFile(null, linkText)) return null;
            var resolved = UrlNormalizer.NormalizeDownloadUrl(url);
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                ResolvedDirectUrl = resolved,
                SourceHost = "OneDrive",
                Confidence = "medium",
                LinkText = linkText
            };
        }

        // Patreon
        if (host.Contains("patreon.com"))
        {
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                SourceHost = "Patreon",
                Confidence = "low",
                RequiresManualStep = true,
                LinkText = linkText
            };
        }

        return null;
    }

    // Attempts to infer archive filename from Google Drive HTML or headers.
    private async Task<string?> TryResolveGoogleDriveFileNameAsync(string viewOrOriginalUrl, string ucDownloadUrl)
    {
        await _resolveLock.WaitAsync();
        try
        {
            if (await TryGoogleDriveNameFromUcAsync(ucDownloadUrl) is { } fromUc)
                return fromUc;
            return TryParseDriveHtml(await _http.GetStringAsync(viewOrOriginalUrl));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Google Drive filename resolution failed for {Url}", viewOrOriginalUrl);
        }
        finally
        {
            _resolveLock.Release();
        }

        return null;
    }

    // Probes usercontent URL headers/page to obtain Google Drive file name.
    private async Task<string?> TryGoogleDriveNameFromUcAsync(string downloadUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (SanitizeDriveArchiveName(RawContentDispositionFileName(resp.Content.Headers)) is { } n)
                return n;
            var mt = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mt.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return null;
            return TryParseDriveHtml(await ReadUtf8PrefixAsync(resp.Content, 786_432));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Google Drive uc download probe failed for {Url}", downloadUrl);
            return null;
        }
    }

    // Normalizes and validates candidate Google Drive filenames as archive names.
    private static string? SanitizeDriveArchiveName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var hint = ExtractArchiveFileNameHint(null, raw) ?? raw.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(hint) || IsNonArchiveFile(hint, null)) return null;
        return ArchiveFileHelper.HasSupportedArchiveExtension(hint) ? hint : null;
    }

    // Parses Google Drive HTML metadata patterns to recover file title/archive name.
    private static string? TryParseDriveHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        foreach (var m in new[]
                 {
                     GoogleDriveTitleRegex().Match(html),
                     GoogleDriveOgTitleRegex().Match(html),
                     GoogleDriveOgTitleAltRegex().Match(html)
                 })
        {
            if (!m.Success) continue;
            if (SanitizeDriveArchiveName(WebUtility.HtmlDecode(m.Groups[1].Value.Trim())) is { } x)
                return x;
        }

        foreach (Match m in GDriveDispositionUtf8StarRegex().Matches(html))
            if (SanitizeDriveArchiveName(Uri.UnescapeDataString(m.Groups[1].Value)) is { } x) return x;

        foreach (Match m in GDriveDispositionQuotedRegex().Matches(html))
            if (SanitizeDriveArchiveName(m.Groups[1].Value) is { } x) return x;

        foreach (Match m in GDriveJsonTitleArchiveRegex().Matches(html))
            if (SanitizeDriveArchiveName(m.Groups[1].Value) is { } x) return x;

        return SanitizeDriveArchiveName(ExtractArchiveFileNameHint(null, html));
    }

    // Reads only a bounded content prefix to avoid loading very large HTML bodies.
    private static async Task<string> ReadUtf8PrefixAsync(HttpContent content, int maxChars)
    {
        using var r = new StreamReader(await content.ReadAsStreamAsync(), Encoding.UTF8, true);
        var buf = new char[8192];
        var sb = new StringBuilder();
        for (var left = maxChars; left > 0;)
        {
            var n = await r.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, left)));
            if (n == 0) break;
            sb.Append(buf, 0, n);
            left -= n;
        }

        return sb.ToString();
    }

    // Extracts filename from Content-Disposition, including encoded fallback patterns.
    private static string? RawContentDispositionFileName(HttpContentHeaders headers)
    {
        try
        {
            var cd = headers.ContentDisposition;
            if (cd != null)
            {
                if (!string.IsNullOrWhiteSpace(cd.FileNameStar)) return cd.FileNameStar.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(cd.FileName)) return cd.FileName.Trim().Trim('"');
            }
        }
        catch { }

        if (!headers.TryGetValues("Content-Disposition", out var parts))
            return null;
        var raw = string.Join("", parts);
        var m = GDriveDispositionUtf8StarRegex().Match(raw);
        if (m.Success)
            return Uri.UnescapeDataString(m.Groups[1].Value);
        m = GDriveDispositionQuotedRegex().Match(raw);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Queries GitHub releases API to find a likely downloadable archive asset.
    private async Task<AssumedDownloadCandidate?> ResolveGitHubReleasesAsync(string url, string? linkText)
    {
        var match = GitHubOwnerRepoRegex().Match(url);
        if (!match.Success) return null;

        var owner = match.Groups[1].Value;
        var repo = match.Groups[2].Value;

        string apiUrl;
        var tagMatch = GitHubReleaseTagRegex().Match(url);
        if (tagMatch.Success)
        {
            var tag = tagMatch.Groups[1].Value;
            apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";
        }
        else if (url.Contains("/releases/latest", StringComparison.OrdinalIgnoreCase))
        {
            apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        }
        else
        {
            apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        }

        await _resolveLock.WaitAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return FallbackGitHubCandidate(url, linkText);

            var json = await resp.Content.ReadAsStringAsync();
            var release = JsonNode.Parse(json);
            var assets = release?["assets"]?.AsArray();
            if (assets == null || assets.Count == 0) return FallbackGitHubCandidate(url, linkText);

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var nameLower = name.ToLowerInvariant();
                if (ArchiveFileHelper.HasSupportedArchiveExtension(nameLower))
                {
                    if (nameLower.Contains("source") && !nameLower.Contains("starsector")) continue;
                    var downloadUrl = asset?["browser_download_url"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        return new AssumedDownloadCandidate
                        {
                            OriginalUrl = url,
                            ResolvedDirectUrl = downloadUrl,
                            SourceHost = "GitHub",
                            FileName = name,
                            Confidence = "high",
                            LinkText = linkText
                        };
                    }
                }
            }

            return FallbackGitHubCandidate(url, linkText);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "GitHub API call failed for {Url}", apiUrl);
            return FallbackGitHubCandidate(url, linkText);
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    // Returns low-confidence fallback when GitHub asset resolution is inconclusive.
    private static AssumedDownloadCandidate FallbackGitHubCandidate(string url, string? linkText)
    {
        return new AssumedDownloadCandidate
        {
            OriginalUrl = url,
            SourceHost = "GitHub",
            Confidence = "low",
            RequiresManualStep = true,
            LinkText = linkText
        };
    }

    // Scrapes MediaFire file pages to find CDN direct links when available.
    private async Task<AssumedDownloadCandidate?> ResolveMediaFireAsync(string url, string? linkText)
    {
        await _resolveLock.WaitAsync();
        try
        {
            var html = WebUtility.HtmlDecode(await _http.GetStringAsync(url));
            var cdnUrl = ExtractMediaFireCdnUrl(html);
            if (!string.IsNullOrEmpty(cdnUrl))
            {
                var fileName = Uri.TryCreate(cdnUrl, UriKind.Absolute, out var cdnUri)
                    ? Uri.UnescapeDataString(cdnUri.Segments.LastOrDefault()?.Trim('/') ?? "")
                    : cdnUrl.Split('/').LastOrDefault();
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = cdnUrl.Split('/').LastOrDefault();

                return new AssumedDownloadCandidate
                {
                    OriginalUrl = url,
                    ResolvedDirectUrl = cdnUrl,
                    SourceHost = "MediaFire",
                    FileName = fileName,
                    Confidence = "medium",
                    LinkText = linkText
                };
            }

            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                SourceHost = "MediaFire",
                Confidence = "low",
                RequiresManualStep = true,
                LinkText = linkText
            };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "MediaFire scrape failed for {Url}", url);
            return new AssumedDownloadCandidate
            {
                OriginalUrl = url,
                SourceHost = "MediaFire",
                Confidence = "low",
                RequiresManualStep = true,
                LinkText = linkText
            };
        }
        finally
        {
            _resolveLock.Release();
        }
    }


    /// <summary>Find direct CDN URL in a MediaFire file page (HTML or embedded JSON).</summary>
    // Extracts direct MediaFire CDN URLs from common HTML/JSON page patterns.
    private static string? ExtractMediaFireCdnUrl(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var m = MediaFireCdnRegex().Match(html);
        if (m.Success)
            return NormalizeMediaFireCdnUrl(m.Value);

        m = MediaFireCdnJsonSlashRegex().Match(html);
        if (m.Success)
            return NormalizeMediaFireCdnUrl(m.Value.Replace("\\/", "/", StringComparison.Ordinal));

        m = MediaFireHrefRegex().Match(html);
        if (m.Success)
            return NormalizeMediaFireCdnUrl(m.Groups[1].Value);

        return null;
    }

    // Cleans scraped MediaFire URL strings into usable absolute download URLs.
    private static string NormalizeMediaFireCdnUrl(string url)
    {
        url = url.Trim().TrimEnd('\\', '"', '\'');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.TrimStart('/');
        return WebUtility.HtmlDecode(url);
    }

    // Loads recently resolved candidate cache from disk on startup.
    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<int, CachedResult>>(json, JsonOpts);
            if (dict == null) return;
            foreach (var kv in dict)
            {
                if (kv.Value.Schema >= AssumedDownloadCacheSchema
                    && !string.IsNullOrEmpty(kv.Value.LinkFingerprint)
                    && DateTime.UtcNow - kv.Value.ResolvedAt < CacheTtl)
                    _cache[kv.Key] = kv.Value;
            }
            _log.Information("Loaded {Count} assumed download cache entries", _cache.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load assumed download cache");
        }
    }

    // Persists in-memory resolution cache so repeated requests are faster.
    private async Task PersistCacheAsync()
    {
        try
        {
            var snapshot = new Dictionary<int, CachedResult>(_cache);
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to persist assumed download cache");
        }
    }

    // Matches GitHub direct release asset URLs, including the stable /releases/latest/download/ form.
    [GeneratedRegex(@"github\.com/[^/]+/[^/]+/(?:releases/(?:download/[^/]+|latest/download)/.+|raw/[^?#]+\.(?:zip|rar|7z|tar\.gz|tar|bz2|gz|xz)|archive/(?:refs/tags/)?[^?#]+\.(?:zip|tar\.gz|tar))", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubDirectAssetRegex();

    // Matches GitHub releases pages that may require API lookup.
    [GeneratedRegex(@"github\.com/[^/]+/[^/]+/releases", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubReleasesRegex();

    // Captures owner/repo segments from GitHub URLs.
    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubOwnerRepoRegex();

    // Captures explicit GitHub release tags from tagged release URLs.
    [GeneratedRegex(@"/releases/tag/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubReleaseTagRegex();

    // Matches plain-text MediaFire CDN links embedded in pages.
    [GeneratedRegex(@"https?://download\d+\.mediafire\.com/[^\s""'<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFireCdnRegex();

    /// <summary>CDN URLs as embedded in JSON (<c>https:\/\/download…<c/>).</summary>
    // Matches escaped MediaFire CDN links embedded in JSON/script blobs.
    [GeneratedRegex(@"https?:(?:\\?/){2}download\d+\.mediafire\.com[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFireCdnJsonSlashRegex();

    // Matches MediaFire CDN URLs specifically inside href attributes.
    [GeneratedRegex(@"href\s*=\s*[""'](https://download\d+\.mediafire\.com[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFireHrefRegex();

    // Captures Google Drive page title content as filename candidate.
    [GeneratedRegex(@"<title>\s*(.*?)\s*(?:-\s*Google Drive)?\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GoogleDriveTitleRegex();

    // Captures og:title metadata from Google Drive pages.
    [GeneratedRegex(@"property\s*=\s*[""']og:title[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleDriveOgTitleRegex();

    // Captures og:title metadata when attribute order is reversed.
    [GeneratedRegex(@"content\s*=\s*[""']([^""']+)[""'][^>]{0,240}?property\s*=\s*[""']og:title[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleDriveOgTitleAltRegex();

    // Captures RFC5987 encoded filename* values from disposition headers/snippets.
    [GeneratedRegex(@"filename\*=UTF-8''([^;\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex GDriveDispositionUtf8StarRegex();

    // Captures quoted filename values from disposition headers/snippets.
    [GeneratedRegex(@"filename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex GDriveDispositionQuotedRegex();

    // Captures archive-like title fields from embedded JSON payloads.
    [GeneratedRegex(@"""title""\s*:\s*""([^""]+\.(?:zip|rar|7z|tar\.gz|tar|bz2))""", RegexOptions.IgnoreCase)]
    private static partial Regex GDriveJsonTitleArchiveRegex();

    // Stores cached resolution output and validation metadata for one topic.
    private class CachedResult
    {
        public List<AssumedDownloadCandidate> Candidates { get; set; } = [];
        public DateTime ResolvedAt { get; set; }
        public int Schema { get; set; }
        /// <summary>Output of <see cref="FingerprintLinks"/> for the link set used when resolving.</summary>
        public string LinkFingerprint { get; set; } = "";
    }
}
