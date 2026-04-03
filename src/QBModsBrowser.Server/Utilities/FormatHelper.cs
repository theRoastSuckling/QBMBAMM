using System.Text.Json;

namespace QBModsBrowser.Server.Utilities;

// Centralizes shared string/number/date formatting and common serializer options used across backend features.
public static class FormatHelper
{
    // Standard write options: indented JSON with camelCase property names, used when persisting data files.
    public static readonly JsonSerializerOptions IndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Converts a UTC timestamp to local time and returns a seconds-precision display string.
    public static string UtcToLocalDateTime(DateTime utcTimestamp)
    {
        var local = DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // Converts a UTC timestamp to local time and returns a compact minute-precision display string.
    public static string UtcToLocalDateTimeCompact(DateTime utcTimestamp)
    {
        var local = DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm");
    }
}
