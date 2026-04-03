namespace QBModsBrowser.Scraper;

// Maps image extensions and MIME types for download/save/serve flows.
public static class ImageFormats
{
    private static readonly Dictionary<string, string> ExtToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
    };

    private static readonly Dictionary<string, string> MimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/bmp"] = ".bmp",
    };

    // Returns MIME type for a known file extension.
    public static string GetMimeType(string extension) =>
        ExtToMime.GetValueOrDefault(extension, "application/octet-stream");

    // Returns preferred file extension for a known MIME type.
    public static string? GetExtension(string? mimeType) =>
        mimeType != null ? MimeToExt.GetValueOrDefault(mimeType) : null;

    // Checks whether an extension is one of the supported image types.
    public static bool IsImageExtension(string extension) =>
        ExtToMime.ContainsKey(extension);

    // Guesses file extension from URL path when headers are unavailable.
    public static string GuessExtensionFromUrl(string url)
    {
        string path = new Uri(url, UriKind.Absolute).AbsolutePath.ToLowerInvariant();
        foreach (var ext in ExtToMime.Keys)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return ext == ".jpeg" ? ".jpg" : ext;
        }
        return ".png";
    }
}

