namespace QBModsBrowser.Server.Utilities;

// Reads and writes a single UTC timestamp to/from a plain-text file (ISO-8601 round-trip format).
// Used by services that need to persist "last run" times across process restarts.
public static class TimestampFile
{
    public static async Task<DateTime?> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        var raw = await File.ReadAllTextAsync(path, ct);
        return DateTime.TryParse(raw.Trim(), out var dt) ? dt.ToUniversalTime() : null;
    }

    public static Task WriteNowAsync(string path, CancellationToken ct = default)
        => File.WriteAllTextAsync(path, DateTime.UtcNow.ToString("O"), ct);
}
