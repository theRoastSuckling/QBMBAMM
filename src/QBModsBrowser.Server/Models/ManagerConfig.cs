namespace QBModsBrowser.Server.Models;

// Persisted manager settings, currently holding the path to the Starsector mods folder.
public class ManagerConfig
{
    // Typical Windows install location; used when no path is stored yet or legacy config was blank.
    public const string DefaultModsPath = @"C:\Program Files (x86)\Fractal Softworks\Starsector\mods";

    public string ModsPath { get; set; } = DefaultModsPath;

    // True only after the user has explicitly saved their mods path; used by the frontend to detect first-run state.
    public bool IsUserConfigured { get; set; } = false;
}

