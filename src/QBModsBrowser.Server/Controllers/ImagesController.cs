using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Scraper;
using QBModsBrowser.Scraper.Storage;
using System.Security.Cryptography;
using System.Text;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/images")]
// Serves locally scraped topic images and optionally caches or redirects external image URLs.
public class ImagesController : ControllerBase
{
    private static readonly HttpClient ExternalImageHttp = BuildExternalImageHttpClient();
    private readonly JsonDataStore _store;

    // Creates the controller with access to the data storage root.
    public ImagesController(JsonDataStore store)
    {
        _store = store;
    }

    // Proxies external image URLs; caches to disk when CacheExternalImages is enabled, redirects otherwise.
    [HttpGet("external")]
    public async Task<IActionResult> GetExternalImage([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("Missing url query parameter.");

        url = System.Net.WebUtility.HtmlDecode(url.Trim());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return BadRequest("Invalid url.");
        if (uri.Scheme is not ("http" or "https"))
            return BadRequest("Only http/https URLs are allowed.");

        var config = await _store.LoadConfig();
        if (!config.CacheExternalImages)
            return Redirect(uri.AbsoluteUri);

        var externalDir = Path.Combine(_store.BasePath, "external-images");
        Directory.CreateDirectory(externalDir);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        var extFromUrl = ImageFormats.GuessExtensionFromUrl(uri.AbsoluteUri);
        var cachePath = Path.Combine(externalDir, $"{hash}{extFromUrl}");

        // Serve cached file immediately when present.
        if (System.IO.File.Exists(cachePath))
            return PhysicalFile(cachePath, DetectContentType(cachePath));

        HttpResponseMessage response;
        try
        {
            response = await ExternalImageHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch
        {
            return NotFound();
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
            return NotFound();

        var extFromType = ImageFormats.GetExtension(response.Content.Headers.ContentType?.MediaType);
        if (!string.IsNullOrWhiteSpace(extFromType))
            cachePath = Path.Combine(externalDir, $"{hash}{extFromType}");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        await System.IO.File.WriteAllBytesAsync(cachePath, bytes, ct);

        return PhysicalFile(cachePath, DetectContentType(cachePath));
    }

    // Returns the file count and total size of the external image cache.
    [HttpGet("external-cache/stats")]
    public IActionResult GetExternalCacheStats()
    {
        var externalDir = Path.Combine(_store.BasePath, "external-images");
        if (!Directory.Exists(externalDir))
            return Ok(new { fileCount = 0, totalSizeBytes = 0L, totalSizeFormatted = "0 B" });

        var files = Directory.GetFiles(externalDir);
        long totalBytes = files.Sum(f => new FileInfo(f).Length);

        string formatted = totalBytes switch
        {
            < 1024 => $"{totalBytes} B",
            < 1024 * 1024 => $"{totalBytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{totalBytes / (1024.0 * 1024):F1} MB",
            _ => $"{totalBytes / (1024.0 * 1024 * 1024):F2} GB"
        };

        return Ok(new { fileCount = files.Length, totalSizeBytes = totalBytes, totalSizeFormatted = formatted });
    }

    // Deletes all files in the external image cache directory.
    [HttpDelete("external-cache")]
    public IActionResult ClearExternalCache()
    {
        var externalDir = Path.Combine(_store.BasePath, "external-images");
        if (!Directory.Exists(externalDir))
            return Ok(new { deleted = 0 });

        var files = Directory.GetFiles(externalDir);
        foreach (var f in files)
        {
            try { System.IO.File.Delete(f); } catch { }
        }

        return Ok(new { deleted = files.Length });
    }

    // Creates a browser-like HTTP client so external hosts return image bytes reliably.
    private static HttpClient BuildExternalImageHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://fractalsoftworks.com/");
        return client;
    }

    // Detects a best-effort content type by sniffing SVG/XML headers first.
    private static string DetectContentType(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[5];
            int read = fs.Read(header);
            if (read >= 4)
            {
                string start = System.Text.Encoding.ASCII.GetString(header[..read]);
                if (start.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                    start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                    return "image/svg+xml";
            }
        }
        catch { }

        return ImageFormats.GetMimeType(Path.GetExtension(path));
    }
}


