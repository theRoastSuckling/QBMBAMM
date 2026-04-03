using System.Globalization;
using System.Text.RegularExpressions;

namespace QBModsBrowser.Scraper;

/// <summary>
/// Compares Starsector-style version strings. Uses decimal comparison for the first two
/// dot-separated parts (so 0.9 &gt; 0.65 and 0.98 &gt; 0.9), then compares any extra segments.
/// </summary>
public static class GameVersionComparer
{
    private static readonly Regex SegmentRegex = new(@"^(\d*)(.*)$", RegexOptions.Compiled);

    // Compares two game version strings using Starsector-specific ordering rules.
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        var aParts = a.Trim().Split('.');
        var bParts = b.Trim().Split('.');

        // Primary: compare major.minor as a single decimal (0.9 vs 0.65 → 0.9 &gt; 0.65)
        if (aParts.Length >= 2 && bParts.Length >= 2
            && IsAllDigits(aParts[0]) && IsAllDigits(aParts[1])
            && IsAllDigits(bParts[0]) && IsAllDigits(bParts[1]))
        {
            if (double.TryParse($"{aParts[0]}.{aParts[1]}", NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
                && double.TryParse($"{bParts[0]}.{bParts[1]}", NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            {
                int c = da.CompareTo(db);
                if (c != 0) return c;
                return CompareTail(aParts, bParts, 2);
            }
        }

        // Fallback: segment-by-segment (handles unusual shapes)
        int max = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < max; i++)
        {
            string ap = i < aParts.Length ? aParts[i].Trim() : "";
            string bp = i < bParts.Length ? bParts[i].Trim() : "";
            int seg = CompareSegment(ap, bp);
            if (seg != 0) return seg;
        }
        return 0;
    }

    // Checks whether a mod's version meets or exceeds a minimum version requirement.
    public static bool IsAtLeast(string? modVersion, string minVersion)
    {
        if (string.IsNullOrWhiteSpace(modVersion)) return false;
        return Compare(modVersion, minVersion) >= 0;
    }

    // Compares version segments after major.minor when decimal comparison ties.
    private static int CompareTail(string[] aParts, string[] bParts, int start)
    {
        int max = Math.Max(aParts.Length, bParts.Length);
        for (int i = start; i < max; i++)
        {
            string ap = i < aParts.Length ? aParts[i].Trim() : "";
            string bp = i < bParts.Length ? bParts[i].Trim() : "";
            int c = CompareSegment(ap, bp);
            if (c != 0) return c;
        }
        return 0;
    }

    // Returns true only when the input segment is composed entirely of digits.
    private static bool IsAllDigits(string s) =>
        s.Length > 0 && s.All(char.IsDigit);

    // Compares one version segment using numeric prefix then suffix fallback.
    private static int CompareSegment(string a, string b)
    {
        var ma = SegmentRegex.Match(a);
        var mb = SegmentRegex.Match(b);
        int an = ma.Success && int.TryParse(ma.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
        int bn = mb.Success && int.TryParse(mb.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
        if (an != bn) return an.CompareTo(bn);

        string asuf = ma.Success ? ma.Groups[2].Value : "";
        string bsuf = mb.Success ? mb.Groups[2].Value : "";
        return string.Compare(asuf, bsuf, StringComparison.OrdinalIgnoreCase);
    }
}


