using System.Text.Json;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Utilities;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Manages mod profile CRUD, equip/unequip bulk operations, and stale-mod cleanup.
public class ModProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;

    private readonly ILogger _log;
    private readonly string _storePath;
    private readonly string _cleanupLogPath;
    private readonly Func<string> _getModsPath;
    private readonly LocalModService _localMods;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ModProfileStore _store = new();
    private bool _loaded;

    // Wires up dependencies for profile persistence and mod-file manipulation.
    public ModProfileService(ILogger logger, string dataPath, Func<string> getModsPath, LocalModService localMods)
    {
        _log = logger.ForContext<ModProfileService>();
        _storePath = Path.Combine(dataPath, "mod-profiles.json");
        _cleanupLogPath = Path.Combine(dataPath, "mod-profile-cleanup.log");
        _getModsPath = getModsPath;
        _localMods = localMods;
    }

    // Returns the full profile store (profiles + state) for the API.
    public async Task<ModProfileStore> GetStoreAsync()
    {
        await EnsureLoadedAsync();
        return _store;
    }

    // Creates a new profile with an auto-generated name and selects it.
    public async Task<ModProfile> CreateProfileAsync()
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var nextNum = 1;
            var existingNames = new HashSet<string>(_store.Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            while (existingNames.Contains($"Profile {nextNum}")) nextNum++;

            var profile = new ModProfile { Name = $"Profile {nextNum}" };
            _store.Profiles.Add(profile);
            _store.State.SelectedProfileId = profile.Id;
            await SaveAsync();
            return profile;
        }
        finally { _lock.Release(); }
    }

    // Renames a profile by ID.
    public async Task<ModProfile?> UpdateProfileAsync(Guid id, string name)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return null;
            profile.Name = name;
            profile.UpdatedAt = DateTime.UtcNow;
            await SaveAsync();
            return profile;
        }
        finally { _lock.Release(); }
    }

    // Deletes a profile. Unequips it if equipped, selects the next if available.
    public async Task<bool> DeleteProfileAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return false;

            if (_store.State.SelectedProfileId == id && _store.State.IsEquipped)
                _store.State.IsEquipped = false;

            _store.Profiles.Remove(profile);
            _store.State.CleanupCounts.Remove(id);

            if (_store.State.SelectedProfileId == id)
            {
                _store.State.SelectedProfileId = _store.Profiles.FirstOrDefault()?.Id;
            }

            await SaveAsync();
            return true;
        }
        finally { _lock.Release(); }
    }

    // Selects a profile without changing equip state.
    public async Task<bool> SelectProfileAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            if (!_store.Profiles.Any(p => p.Id == id)) return false;
            _store.State.SelectedProfileId = id;
            await SaveAsync();
            return true;
        }
        finally { _lock.Release(); }
    }

    // Equips a profile: disables all enabled mods, then enables the profile's mods in bulk.
    public async Task<bool> EquipProfileAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return false;

            var modsPath = _getModsPath();
            var localMods = _localMods.GetCachedMods();

            var enabledModsForFile = new List<string>();

            foreach (var modId in profile.ModIds)
            {
                var mod = localMods.FirstOrDefault(m =>
                    string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
                if (mod != null)
                {
                    enabledModsForFile.Add(mod.ModId);
                }
            }

            await WriteEnabledModsJsonAsync(modsPath, enabledModsForFile);
            await _localMods.ScanAsync();

            _store.State.SelectedProfileId = id;
            _store.State.IsEquipped = true;
            await SaveAsync();

            _log.Information("Equipped profile {Name} ({Id}) with {Count} mods", profile.Name, id, enabledModsForFile.Count);
            return true;
        }
        finally { _lock.Release(); }
    }

    // Unequips the active profile without changing mod enabled states.
    public async Task UnequipAsync()
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            _store.State.IsEquipped = false;
            await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    // Adds all currently enabled local mods to the specified profile (union).
    public async Task<ModProfile?> AddEnabledModsAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return null;

            var enabled = _localMods.GetCachedMods()
                .Where(m => m.IsEnabled)
                .Select(m => m.ModId);

            var existing = new HashSet<string>(profile.ModIds, StringComparer.OrdinalIgnoreCase);
            foreach (var modId in enabled)
            {
                if (existing.Add(modId))
                    profile.ModIds.Add(modId);
            }

            profile.UpdatedAt = DateTime.UtcNow;
            await SaveAsync();
            return profile;
        }
        finally { _lock.Release(); }
    }

    // Replaces the profile's mod list with all currently enabled local mods.
    public async Task<ModProfile?> OverwriteWithEnabledModsAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return null;

            profile.ModIds = _localMods.GetCachedMods()
                .Where(m => m.IsEnabled)
                .Select(m => m.ModId)
                .ToList();

            profile.UpdatedAt = DateTime.UtcNow;
            await SaveAsync();
            return profile;
        }
        finally { _lock.Release(); }
    }

    // Syncs a single mod toggle to the equipped profile (add or remove one mod ID).
    public async Task OnModToggledAsync(string modId, bool isEnabled)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            if (!_store.State.IsEquipped || _store.State.SelectedProfileId == null) return;

            var profile = _store.Profiles.FirstOrDefault(p => p.Id == _store.State.SelectedProfileId);
            if (profile == null) return;

            if (isEnabled)
            {
                if (!profile.ModIds.Contains(modId, StringComparer.OrdinalIgnoreCase))
                    profile.ModIds.Add(modId);
            }
            else
            {
                profile.ModIds.RemoveAll(id => id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            }

            profile.UpdatedAt = DateTime.UtcNow;
            await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    // Removes a mod from every profile (called when a mod is deleted from disk).
    public async Task RemoveModFromAllProfilesAsync(string modId)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            bool changed = false;
            foreach (var profile in _store.Profiles)
            {
                int removed = profile.ModIds.RemoveAll(id => id.Equals(modId, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    profile.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }
            if (changed) await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    // Prunes mod IDs that are no longer present locally, logs removals, tracks cleanup counts.
    public async Task CleanupMissingModsAsync(IReadOnlyCollection<string> scannedModIds)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var known = new HashSet<string>(scannedModIds, StringComparer.OrdinalIgnoreCase);
            var logLines = new List<string>();
            bool changed = false;
            var timestamp = FormatHelper.UtcToLocalDateTimeCompact(DateTime.UtcNow);

            foreach (var profile in _store.Profiles)
            {
                var missing = profile.ModIds.Where(id => !known.Contains(id)).ToList();
                if (missing.Count == 0) continue;

                foreach (var modId in missing)
                {
                    profile.ModIds.Remove(modId);
                    logLines.Add($"[{timestamp}] Profile \"{profile.Name}\" ({profile.Id}): removed mod \"{modId}\"");
                }

                profile.UpdatedAt = DateTime.UtcNow;

                if (!_store.State.CleanupCounts.TryGetValue(profile.Id, out int existing))
                    existing = 0;
                _store.State.CleanupCounts[profile.Id] = existing + missing.Count;

                changed = true;
                _log.Information("Profile {Name}: cleaned up {Count} missing mods", profile.Name, missing.Count);
            }

            if (changed)
            {
                await SaveAsync();
                if (logLines.Count > 0)
                {
                    try { await File.AppendAllLinesAsync(_cleanupLogPath, logLines); }
                    catch (Exception ex) { _log.Warning(ex, "Failed to write profile cleanup log"); }
                }
            }
        }
        finally { _lock.Release(); }
    }

    // Clears the cleanup warning count for a profile.
    public async Task<bool> DismissCleanupAsync(Guid id)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            if (!_store.State.CleanupCounts.ContainsKey(id)) return false;
            _store.State.CleanupCounts.Remove(id);
            await SaveAsync();
            return true;
        }
        finally { _lock.Release(); }
    }

    // Writes the Starsector enabled_mods.json with the given mod ID list.
    private static async Task WriteEnabledModsJsonAsync(string modsPath, List<string> modIds)
    {
        var path = Path.Combine(modsPath, "enabled_mods.json");
        var data = new { enabledMods = modIds };
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    // Loads the profile store from disk on first access.
    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;
            if (File.Exists(_storePath))
            {
                var json = await File.ReadAllTextAsync(_storePath);
                _store = JsonSerializer.Deserialize<ModProfileStore>(json, JsonOpts) ?? new ModProfileStore();
            }
            _loaded = true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load mod profiles, starting fresh");
            _store = new ModProfileStore();
            _loaded = true;
        }
        finally { _lock.Release(); }
    }

    // Persists the current profile store to disk. Caller must hold _lock.
    private async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_store, JsonOpts);
            await File.WriteAllTextAsync(_storePath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to save mod profiles");
        }
    }
}
