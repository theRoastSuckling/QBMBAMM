using System.Text.Json;
using System.Text.RegularExpressions;
using QBModsBrowser.Server.Models;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Loads and indexes StarsectorModRepo data so forum topics can be matched to curated metadata.
public partial class ModRepoService
{
    private const string RepoUrl = "https://github.com/wispborne/StarsectorModRepo/raw/refs/heads/main/ModRepo.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _cachePath;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ModRepoData? _cache;
    private DateTime _cacheTime;
    private Dictionary<int, ModRepoEntry>? _topicIndex;
    private Dictionary<int, ModRepoEntry>? _nexusIndex;
    // Maps lowercase mod name to forum topic id for dependency name matching.
    private Dictionary<string, int>? _nameIndex;

    // Creates the service and prepares HTTP/cache paths used by background refreshes.
    public ModRepoService(ILogger logger, string dataPath)
    {
        _log = logger.ForContext<ModRepoService>();
        _cachePath = Path.Combine(dataPath, "mod-repo-cache.json");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QBModsBrowser/1.0");
    }

    // Returns cached repo data, refreshing from GitHub or disk when stale or forced.
    public async Task<ModRepoData> GetDataAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheTime < CacheTtl)
            return _cache;

        await _lock.WaitAsync();
        try
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheTime < CacheTtl)
                return _cache;

            ModRepoData? data = null;

            try
            {
                _log.Information("Fetching ModRepo.json from GitHub...");
                var json = await _http.GetStringAsync(RepoUrl);
                data = JsonSerializer.Deserialize<ModRepoData>(json, JsonOpts);
                if (data != null)
                {
                    await File.WriteAllTextAsync(_cachePath, json);
                    _log.Information("ModRepo.json fetched and cached: {Count} items", data.Items.Count);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to fetch ModRepo.json from GitHub, trying disk cache");
            }

            if (data == null && File.Exists(_cachePath))
            {
                try
                {
                    var diskJson = await File.ReadAllTextAsync(_cachePath);
                    data = JsonSerializer.Deserialize<ModRepoData>(diskJson, JsonOpts);
                    _log.Information("Loaded ModRepo.json from disk cache: {Count} items", data?.Items.Count ?? 0);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to load ModRepo.json from disk cache");
                }
            }

            data ??= new ModRepoData();
            _cache = data;
            _cacheTime = DateTime.UtcNow;
            RebuildIndexes(data);
            return data;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Exposes the topic-id index used by matching and enrichment flows.
    public Dictionary<int, ModRepoEntry> GetTopicIndex()
    {
        return _topicIndex ?? new();
    }

    // Exposes the nexus-id index used when forum topic mapping is missing.
    public Dictionary<int, ModRepoEntry> GetNexusIndex()
    {
        return _nexusIndex ?? new();
    }

    // Looks up a single repo entry by forum topic id for quick controller access.
    public ModRepoEntry? FindByTopicId(int topicId)
    {
        return _topicIndex != null && _topicIndex.TryGetValue(topicId, out var entry) ? entry : null;
    }

    // Returns the name-to-topicId index for dependency name matching in DependencyService.
    public Dictionary<string, int> GetNameIndex() => _nameIndex ?? new();

    // Rebuilds lookup indexes after each data refresh so later reads stay fast.
    private void RebuildIndexes(ModRepoData data)
    {
        var topicIdx = new Dictionary<int, ModRepoEntry>();
        var nexusIdx = new Dictionary<int, ModRepoEntry>();
        // Case-insensitive name→topicId for dependency name matching.
        var nameIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in data.Items)
        {
            int? entryTopicId = null;
            var forumUrl = entry.GetUrl("Forum");
            if (!string.IsNullOrEmpty(forumUrl))
            {
                var match = TopicIdRegex().Match(forumUrl);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int tid))
                {
                    entryTopicId = tid;
                    if (!topicIdx.ContainsKey(tid))
                        topicIdx[tid] = entry;
                    else
                        topicIdx[tid] = PickBetterEntry(topicIdx[tid], entry);
                }
            }

            var nexusUrl = entry.GetUrl("NexusMods");
            if (!string.IsNullOrEmpty(nexusUrl))
            {
                var match = NexusIdRegex().Match(nexusUrl);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int nid))
                {
                    if (!nexusIdx.ContainsKey(nid))
                        nexusIdx[nid] = entry;
                }
            }

            // Index by name only when a forum topic id was found for this entry.
            if (entryTopicId.HasValue && !string.IsNullOrEmpty(entry.Name) && !nameIdx.ContainsKey(entry.Name))
                nameIdx[entry.Name] = entryTopicId.Value;
        }

        _topicIndex = topicIdx;
        _nexusIndex = nexusIdx;
        _nameIndex = nameIdx;
        _log.Information("ModRepo indexes built: {TopicCount} by topic, {NexusCount} by nexus, {NameCount} by name",
            topicIdx.Count, nexusIdx.Count, nameIdx.Count);
    }

    // Chooses the richer duplicate entry when multiple repo items map to one topic.
    private static ModRepoEntry PickBetterEntry(ModRepoEntry existing, ModRepoEntry candidate)
    {
        int existingScore = ScoreEntry(existing);
        int candidateScore = ScoreEntry(candidate);
        return candidateScore > existingScore ? candidate : existing;
    }

    // Scores entry completeness so duplicate resolution prefers more useful metadata.
    private static int ScoreEntry(ModRepoEntry e)
    {
        int score = 0;
        if (!string.IsNullOrEmpty(e.Summary)) score++;
        if (!string.IsNullOrEmpty(e.ModVersion)) score++;
        if (e.GetUrl("DirectDownload") != null) score += 2;
        if (e.Images != null && e.Images.Count > 0) score++;
        if (!string.IsNullOrEmpty(e.DateTimeEdited)) score++;
        return score;
    }

    // Extracts forum topic id from known forum URL query format.
    [GeneratedRegex(@"topic=(\d+)", RegexOptions.Compiled)]
    private static partial Regex TopicIdRegex();

    // Extracts NexusMods numeric id for cross-source matching.
    [GeneratedRegex(@"/mods/(\d+)", RegexOptions.Compiled)]
    private static partial Regex NexusIdRegex();
}
