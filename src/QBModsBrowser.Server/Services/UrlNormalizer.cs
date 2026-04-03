using System.Text.RegularExpressions;

namespace QBModsBrowser.Server.Services;

// Normalizes hosting-specific URLs into stable direct-download or raw-content forms.
public static partial class UrlNormalizer
{
    // Normalizes user-facing download links into direct file URLs when host rules are known.
    public static string NormalizeDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        url = url.Trim();

        if (IsGoogleDrive(url))
            return NormalizeGoogleDrive(url);

        if (IsDropbox(url))
            return NormalizeDropbox(url);

        if (IsOneDrive(url))
            return NormalizeOneDrive(url);

        if (IsGitHubBlob(url))
            return NormalizeGitHubBlob(url);

        return url;
    }

    // Normalizes version-check URLs so remote .version files can be fetched reliably.
    public static string NormalizeVersionFileUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        url = url.Trim();

        if (IsGitHubBlob(url))
            return NormalizeGitHubBlob(url);

        if (IsDropbox(url))
            return NormalizeDropbox(url);

        return url;
    }

    // Detects Google Drive URLs that require query/path normalization.
    private static bool IsGoogleDrive(string url) =>
        url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);

    // Converts Google Drive links into direct export endpoints with confirm flag.
    private static string NormalizeGoogleDrive(string url)
    {
        // Extract file ID from /file/d/{id} pattern
        var fileIdMatch = GoogleDriveFileIdRegex().Match(url);
        if (fileIdMatch.Success)
        {
            var fileId = fileIdMatch.Groups[1].Value;
            return $"https://drive.google.com/uc?export=download&id={fileId}";
        }

        // Handle open?id= pattern
        if (url.Contains("open?id=", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Replace("open?id=", "uc?id=", StringComparison.OrdinalIgnoreCase);
            if (!url.Contains("export=download", StringComparison.OrdinalIgnoreCase))
                url += "&export=download";
        }

        return url;
    }

    // Detects Dropbox links for dl parameter conversion.
    private static bool IsDropbox(string url) =>
        url.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase);

    // Forces Dropbox links into direct-download mode.
    private static string NormalizeDropbox(string url)
    {
        if (url.Contains("dl=0", StringComparison.OrdinalIgnoreCase))
            return url.Replace("dl=0", "dl=1", StringComparison.OrdinalIgnoreCase);

        if (!url.Contains("dl=1", StringComparison.OrdinalIgnoreCase))
            return url + (url.Contains('?') ? "&dl=1" : "?dl=1");

        return url;
    }

    // Detects OneDrive links for download query normalization.
    private static bool IsOneDrive(string url) =>
        url.Contains("onedrive.live.com", StringComparison.OrdinalIgnoreCase);

    // Appends OneDrive direct-download query flag when missing.
    private static string NormalizeOneDrive(string url)
    {
        if (!url.Contains("download=1", StringComparison.OrdinalIgnoreCase))
            return url + (url.Contains('?') ? "&download=1" : "?download=1");
        return url;
    }

    // Detects GitHub blob URLs that should be converted to raw content URLs.
    private static bool IsGitHubBlob(string url) =>
        url.Contains("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("/blob/", StringComparison.OrdinalIgnoreCase);

    // Rewrites GitHub blob links to raw.githubusercontent.com equivalents.
    private static string NormalizeGitHubBlob(string url)
    {
        return url
            .Replace("github.com", "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            .Replace("/blob/", "/", StringComparison.OrdinalIgnoreCase);
    }

    // Returns true when the URL host requires a non-HTTP client and cannot be auto-downloaded by this app.
    // Use this to reject such URLs before assigning them as direct download candidates.
    public static bool IsUnsupportedAutoDownloadHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        // mega.nz uses an encrypted client-side protocol; standard HTTP cannot fetch the file.
        return host.Contains("mega.nz") || host.Contains("mega.co.nz");
    }

    // Extracts Google Drive file id from /file/d/... URLs.
    [GeneratedRegex(@"/file/d/([a-zA-Z0-9_-]+)", RegexOptions.Compiled)]
    private static partial Regex GoogleDriveFileIdRegex();
}


