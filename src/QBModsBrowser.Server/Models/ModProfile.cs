namespace QBModsBrowser.Server.Models;

// Represents a named set of mod IDs that can be equipped as a group.
public class ModProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> ModIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Tracks which profile is selected and whether it is actively equipped.
public class ModProfileState
{
    public Guid? SelectedProfileId { get; set; }
    public bool IsEquipped { get; set; }
    public Dictionary<Guid, int> CleanupCounts { get; set; } = new();
}

// Root container for on-disk profile persistence in mod-profiles.json.
public class ModProfileStore
{
    public List<ModProfile> Profiles { get; set; } = [];
    public ModProfileState State { get; set; } = new();
}
