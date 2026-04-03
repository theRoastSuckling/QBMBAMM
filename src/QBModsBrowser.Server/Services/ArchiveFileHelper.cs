namespace QBModsBrowser.Server.Services;

// Centralizes archive extension checks and archive filename normalization.
public static class ArchiveFileHelper
{
    // Returns true when the provided file/path looks like a supported archive.
    public static bool HasSupportedArchiveExtension(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
            return false;

        var value = pathOrFileName.Trim().ToLowerInvariant();
        return value.EndsWith(".zip")
            || value.EndsWith(".rar")
            || value.EndsWith(".7z")
            || value.EndsWith(".tar.gz")
            || value.EndsWith(".tar")
            || value.EndsWith(".bz2")
            || value.EndsWith(".gz")
            || value.EndsWith(".xz");
    }

    // Normalizes archive file names to the likely extracted folder base name.
    public static string GetArchiveBaseName(string pathOrFileName)
    {
        var name = Path.GetFileName(pathOrFileName);
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz"))
            return name[..^7];
        if (lower.EndsWith(".tar"))
            return name[..^4];
        if (lower.EndsWith(".zip") || lower.EndsWith(".rar"))
            return name[..^4];
        if (lower.EndsWith(".7z") || lower.EndsWith(".bz2") || lower.EndsWith(".gz") || lower.EndsWith(".xz"))
            return Path.GetFileNameWithoutExtension(name);
        return Path.GetFileNameWithoutExtension(name);
    }
}
