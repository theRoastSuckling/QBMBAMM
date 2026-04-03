using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace QBModsBrowser.Scraper.Services;

// Downloads forum-hosted images and stores them under each topic's image directory.
public class ImageDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _baseDataPath;

    // Creates the image downloader and configures HTTP headers for forum hosts.
    public ImageDownloader(ILogger logger, string baseDataPath)
    {
        _log = logger.ForContext<ImageDownloader>();
        _baseDataPath = baseDataPath;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    // Downloads one image and returns the local filename, or null on failure/skip.
    public async Task<string?> DownloadImage(string imageUrl, int topicId, CancellationToken ct)
    {
        try
        {
            // Decode HTML entities (&amp; -> &) before building the URL
            string decodedUrl = WebUtility.HtmlDecode(imageUrl);

            string absoluteUrl = decodedUrl;
            if (!decodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                absoluteUrl = decodedUrl.StartsWith("//")
                    ? "https:" + decodedUrl
                    : $"https://{ForumConstants.ForumHost}" + (decodedUrl.StartsWith('/') ? "" : "/") + decodedUrl;
            }

            // Safety gate: never download images from external hosts.
            if (!ForumConstants.IsForumHosted(absoluteUrl))
            {
                _log.Debug("Skipping external image: {Url}", absoluteUrl);
                return null;
            }

            // Hash the decoded URL for the filename
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(absoluteUrl)))[..16].ToLowerInvariant();
            string ext = ImageFormats.GuessExtensionFromUrl(absoluteUrl);
            string filename = $"{hash}{ext}";

            string dir = Path.Combine(_baseDataPath, "mods", topicId.ToString(), "images");
            Directory.CreateDirectory(dir);
            string fullPath = Path.Combine(dir, filename);

            if (File.Exists(fullPath))
            {
                _log.Debug("Image already downloaded: {File}", filename);
                return filename;
            }

            var response = await _http.GetAsync(absoluteUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Failed to download image {Url}: {Status}", absoluteUrl, response.StatusCode);
                return null;
            }

            // Use content-type to correct extension for dynamic images (shields.io, etc.)
            string? actualExt = ImageFormats.GetExtension(response.Content.Headers.ContentType?.MediaType);
            if (actualExt != null && actualExt != ext)
            {
                ext = actualExt;
                filename = $"{hash}{ext}";
                fullPath = Path.Combine(dir, filename);
                if (File.Exists(fullPath))
                {
                    _log.Debug("Image already downloaded: {File}", filename);
                    return filename;
                }
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            await File.WriteAllBytesAsync(fullPath, bytes, ct);

            _log.Debug("Downloaded image: {Url} -> {File} ({Bytes} bytes)", absoluteUrl, filename, bytes.Length);
            return filename;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error downloading image {Url}", imageUrl);
            return null;
        }
    }

    // Disposes the HTTP client used for image fetches.
    public void Dispose() => _http.Dispose();
}

