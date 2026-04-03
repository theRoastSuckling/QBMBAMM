namespace QBModsBrowser.Server.Models;

// Root result of a dependency scan — matched topic priorities and unmatched ghost entries.
public class DependencyReport
{
    // Forum topics that are required as dependencies but not currently installed.
    public List<DependencyTopicMatch> PriorityTopicIds { get; set; } = [];

    // Dependencies that could not be matched to any known forum topic.
    public List<UnmatchedDependency> UnmatchedDependencies { get; set; } = [];
}

// A forum topic identified as an uninstalled dependency, plus which local mods need it.
public class DependencyTopicMatch
{
    public int TopicId { get; set; }

    // Local mods that declared this topic's mod as a dependency.
    public List<DependencyRequesterInfo> Requesters { get; set; } = [];
}

// A dependency that could not be matched to a forum topic — shown as a ghost card.
public class UnmatchedDependency
{
    // Mod id from the dependency declaration (may be null if only name was provided).
    public string? Id { get; set; }

    // Human-readable name from the dependency declaration.
    public string? Name { get; set; }

    // Local mods that declared this unresolved dependency.
    public List<DependencyRequesterInfo> Requesters { get; set; } = [];
}

// Identifies a locally installed mod that requires a given dependency.
public class DependencyRequesterInfo
{
    public string ModId { get; set; } = "";
    public string? Name { get; set; }

    // Forum topic id of the requester mod, if known (null when unmatched).
    public int? TopicId { get; set; }
}
