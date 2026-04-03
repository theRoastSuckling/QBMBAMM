using QBModsBrowser.Server.Models;

namespace QBModsBrowser.Server.Services;

// Computes which installed mod dependencies are missing and resolves them to forum topics where possible.
public class DependencyService
{
    private readonly LocalModService _localMods;
    private readonly ModMatchingService _matching;
    private readonly ModRepoService _modRepo;

    // Wires the three data sources needed for dependency resolution.
    public DependencyService(LocalModService localMods, ModMatchingService matching, ModRepoService modRepo)
    {
        _localMods = localMods;
        _matching = matching;
        _modRepo = modRepo;
    }

    // Scans all installed mods for unresolved dependencies and matches them to forum topics.
    // Returns priority topic matches (yellow-border cards) and unmatched ghost entries.
    public DependencyReport ComputeReport()
    {
        var localMods = _localMods.GetCachedMods();
        var byModId = _localMods.GetByModIdIndex();

        // Build a set of installed mod names for name-only dependency checks.
        var installedNames = new HashSet<string>(
            localMods.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n))!,
            StringComparer.OrdinalIgnoreCase);

        var persistedMatches = _matching.GetPersistedMatches();
        var nameIndex = _modRepo.GetNameIndex();

        // Group uninstalled dependencies by their identity key (id ?? name).
        // Multiple mods can require the same dep, so we collect all requesters per dep.
        var depGroups = new Dictionary<string, (ModDependency Dep, List<LocalMod> Requesters)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var mod in localMods)
        {
            if (mod.Dependencies == null || mod.Dependencies.Count == 0) continue;

            foreach (var dep in mod.Dependencies)
            {
                var key = dep.Id ?? dep.Name;
                if (string.IsNullOrEmpty(key)) continue;

                // Skip if the dependency is already installed (check by id then by name).
                if (dep.Id != null && byModId.ContainsKey(dep.Id)) continue;
                if (dep.Id == null && dep.Name != null && installedNames.Contains(dep.Name)) continue;

                if (!depGroups.TryGetValue(key, out var group))
                {
                    group = (dep, []);
                    depGroups[key] = group;
                }
                group.Requesters.Add(mod);
            }
        }

        var priorityTopicIds = new List<DependencyTopicMatch>();
        var unmatched = new List<UnmatchedDependency>();

        // Track topic ids we've already added to avoid duplicate cards.
        var seenTopicIds = new HashSet<int>();

        foreach (var (_, (dep, requesters)) in depGroups)
        {
            var requesterInfos = BuildRequesterInfos(requesters, persistedMatches);

            // Step 1: match via mod-matches.json by dep id.
            if (dep.Id != null && persistedMatches.TryGetValue(dep.Id, out int matchedTid))
            {
                if (seenTopicIds.Add(matchedTid))
                    priorityTopicIds.Add(new DependencyTopicMatch { TopicId = matchedTid, Requesters = requesterInfos });
                continue;
            }

            // Step 2: match via ModRepo name index.
            if (dep.Name != null && nameIndex.TryGetValue(dep.Name, out int repoTid))
            {
                if (seenTopicIds.Add(repoTid))
                    priorityTopicIds.Add(new DependencyTopicMatch { TopicId = repoTid, Requesters = requesterInfos });
                continue;
            }

            // No match found — emit as ghost card.
            unmatched.Add(new UnmatchedDependency
            {
                Id = dep.Id,
                Name = dep.Name,
                Requesters = requesterInfos
            });
        }

        return new DependencyReport
        {
            PriorityTopicIds = priorityTopicIds,
            UnmatchedDependencies = unmatched
        };
    }

    // Converts a list of requesting local mods into requester info DTOs with topic ids where known.
    private List<DependencyRequesterInfo> BuildRequesterInfos(
        List<LocalMod> requesters,
        IReadOnlyDictionary<string, int> persistedMatches)
    {
        var infos = new List<DependencyRequesterInfo>(requesters.Count);
        foreach (var mod in requesters)
        {
            // Try persisted matches first, then the topic index.
            int? topicId = null;
            if (persistedMatches.TryGetValue(mod.ModId, out int pid))
                topicId = pid;
            else
                topicId = _localMods.GetTopicIdForModId(mod.ModId);

            infos.Add(new DependencyRequesterInfo
            {
                ModId = mod.ModId,
                Name = mod.Name,
                TopicId = topicId
            });
        }
        return infos;
    }
}
