namespace QBModsBrowser.Server.Models;

// Result of installing a mod archive: the extracted mod IDs and their on-disk folder paths.
public sealed class ModArchiveInstallResult
{
    public List<string> ModIds { get; } = [];
    public List<string> InstalledFolderPaths { get; } = [];
}

