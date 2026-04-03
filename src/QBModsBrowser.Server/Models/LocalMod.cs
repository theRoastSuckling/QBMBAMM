namespace QBModsBrowser.Server.Models;

// Represents a mod folder found in the local Starsector mods directory.
public class LocalMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? GameVersion { get; set; }
    public string FolderName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsUtility { get; set; }
    public bool IsTotalConversion { get; set; }
    public string? Description { get; set; }
    public List<ModDependency>? Dependencies { get; set; }
    public VersionCheckerInfo? VersionChecker { get; set; }
}

// A single dependency entry from a mod's mod_info.json dependencies list.
public class ModDependency
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
}

// Data parsed from a mod's .version file used to locate and compare remote version info.
public class VersionCheckerInfo
{
    public string? ModName { get; set; }
    public string? MasterVersionFile { get; set; }
    public VersionObject? ModVersion { get; set; }
    public string? ModThreadId { get; set; }
    public string? ModNexusId { get; set; }
    public string? DirectDownloadURL { get; set; }
    public string? ChangelogURL { get; set; }
}

// Structured version number with major/minor/patch parts supporting numeric comparison.
public class VersionObject
{
    public string? Major { get; set; }
    public string? Minor { get; set; }
    public string? Patch { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Major != null) parts.Add(Major);
        if (Minor != null) parts.Add(Minor);
        if (Patch != null) parts.Add(Patch);
        return string.Join(".", parts);
    }

    public int CompareTo(VersionObject? other)
    {
        if (other == null) return 1;
        int cmp = CompareField(Major, other.Major);
        if (cmp != 0) return cmp;
        cmp = CompareField(Minor, other.Minor);
        if (cmp != 0) return cmp;
        return CompareField(Patch, other.Patch);
    }

    private static int CompareField(string? a, string? b)
    {
        int ai = 0, bi = 0;
        if (a != null) int.TryParse(a, out ai);
        if (b != null) int.TryParse(b, out bi);
        return ai.CompareTo(bi);
    }
}


