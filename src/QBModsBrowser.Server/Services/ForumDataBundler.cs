using QBModsBrowser.Scraper.Models;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Converts between the local data/ folder layout and the portable ForumDataBundle JSON format.
public class ForumDataBundler
{
    private readonly ILogger _log;

    // Accepts a logger for progress/error reporting during bundle creation and unpacking.
    public ForumDataBundler(ILogger logger)
    {
        _log = logger.ForContext<ForumDataBundler>();
    }

    // Packs the current data/ folder contents into a single portable bundle.
    // LocalPath on image refs is stripped so the bundle contains no machine-specific paths.
    public async Task<ForumDataBundle> CreateBundleAsync(JsonDataStore store, AssumedDownloadService assumed)
    {
        _log.Information("Creating forum data bundle...");

        var index = await store.LoadIndex();
        var details = new Dictionary<int, ModDetail>(index.Count);

        foreach (var summary in index)
        {
            var detail = await store.LoadDetail(summary.TopicId);
            if (detail == null) continue;

            // Strip local file paths — meaningless on remote machines; clients use the image proxy.
            foreach (var img in detail.Images)
                img.LocalPath = "";

            details[summary.TopicId] = detail;
        }

        var assumedDownloads = assumed.GetAllCandidates();

        var updatedAt = index.Count > 0
            ? index.Max(s => s.ScrapedAt)
            : DateTime.UtcNow;

        var bundle = new ForumDataBundle
        {
            UpdatedAt = updatedAt,
            Index = index,
            Details = details,
            AssumedDownloads = assumedDownloads
        };

        _log.Information(
            "Bundle created: {ModCount} mods, {DetailCount} details, {AssumedCount} assumed-download entries, updatedAt={UpdatedAt:u}",
            index.Count, details.Count, assumedDownloads.Count, updatedAt);

        return bundle;
    }

    // Unpacks a remote bundle into the local data/ folder, overwriting existing files.
    // Called on client machines when the remote bundle is fresher than the local data.
    public async Task UnpackBundleAsync(ForumDataBundle bundle, JsonDataStore store, AssumedDownloadService assumed)
    {
        _log.Information(
            "Unpacking forum data bundle: {ModCount} mods, updatedAt={UpdatedAt:u}",
            bundle.Index.Count, bundle.UpdatedAt);

        await store.SaveIndex(bundle.Index);

        foreach (var (topicId, detail) in bundle.Details)
        {
            try
            {
                await store.SaveDetail(detail);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to save detail for topic {TopicId} during unpack", topicId);
            }
        }

        assumed.ImportCandidates(bundle.AssumedDownloads);

        _log.Information("Bundle unpack complete");
    }
}
