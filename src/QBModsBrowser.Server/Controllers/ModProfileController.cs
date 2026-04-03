using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Server.Services;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/profiles")]
// Exposes mod profile CRUD, equip/unequip, and cleanup endpoints.
public class ModProfileController : ControllerBase
{
    private readonly ModProfileService _profiles;

    public ModProfileController(ModProfileService profiles)
    {
        _profiles = profiles;
    }

    // Returns the full profile store (all profiles + selection/equip state).
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return Ok(await _profiles.GetStoreAsync());
    }

    // Creates a new profile with an auto-generated name and selects it.
    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var profile = await _profiles.CreateProfileAsync();
        return Ok(profile);
    }

    // Renames an existing profile.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProfileUpdateRequest request)
    {
        var result = await _profiles.UpdateProfileAsync(id, request.Name ?? "");
        return result != null ? Ok(result) : NotFound(new { error = "Profile not found" });
    }

    // Deletes a profile, unequipping it if currently equipped.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        return await _profiles.DeleteProfileAsync(id)
            ? Ok(await _profiles.GetStoreAsync())
            : NotFound(new { error = "Profile not found" });
    }

    // Selects a profile as active without changing equip state.
    [HttpPost("{id:guid}/select")]
    public async Task<IActionResult> Select(Guid id)
    {
        return await _profiles.SelectProfileAsync(id)
            ? Ok(await _profiles.GetStoreAsync())
            : NotFound(new { error = "Profile not found" });
    }

    // Equips a profile: bulk-disables all mods, then enables the profile's mods.
    [HttpPost("{id:guid}/equip")]
    public async Task<IActionResult> Equip(Guid id)
    {
        return await _profiles.EquipProfileAsync(id)
            ? Ok(await _profiles.GetStoreAsync())
            : NotFound(new { error = "Profile not found" });
    }

    // Unequips the currently equipped profile.
    [HttpPost("unequip")]
    public async Task<IActionResult> Unequip()
    {
        await _profiles.UnequipAsync();
        return Ok(await _profiles.GetStoreAsync());
    }

    // Adds all currently enabled mods to the specified profile (union merge).
    [HttpPost("{id:guid}/add-enabled")]
    public async Task<IActionResult> AddEnabled(Guid id)
    {
        var result = await _profiles.AddEnabledModsAsync(id);
        return result != null ? Ok(await _profiles.GetStoreAsync()) : NotFound(new { error = "Profile not found" });
    }

    // Overwrites the profile's mod list with currently enabled mods.
    [HttpPost("{id:guid}/overwrite-enabled")]
    public async Task<IActionResult> OverwriteEnabled(Guid id)
    {
        var result = await _profiles.OverwriteWithEnabledModsAsync(id);
        return result != null ? Ok(await _profiles.GetStoreAsync()) : NotFound(new { error = "Profile not found" });
    }

    // Syncs a single mod toggle into the equipped profile.
    [HttpPost("sync-toggle")]
    public async Task<IActionResult> SyncToggle([FromBody] SyncToggleRequest request)
    {
        await _profiles.OnModToggledAsync(request.ModId, request.IsEnabled);
        return Ok(new { message = "ok" });
    }

    // Clears the cleanup warning counter for a profile.
    [HttpPost("{id:guid}/dismiss-cleanup")]
    public async Task<IActionResult> DismissCleanup(Guid id)
    {
        await _profiles.DismissCleanupAsync(id);
        return Ok(await _profiles.GetStoreAsync());
    }
}

// Carries the new name for a profile rename request.
public class ProfileUpdateRequest
{
    public string? Name { get; set; }
}

// Carries a single mod toggle event for equipped-profile sync.
public class SyncToggleRequest
{
    public string ModId { get; set; } = "";
    public bool IsEnabled { get; set; }
}
