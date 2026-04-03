using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;
using QBModsBrowser.Server.Utilities;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/manager")]
// Handles mod manager operations: folder scanning, downloads, enable/disable/delete, and config.
public class ModManagerController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;

    private readonly string _configPath;
    private readonly LocalModService _localMods;
    private readonly ModRepoService _modRepo;
    private readonly ModMatchingService _matching;
    private readonly VersionCheckerService _versionChecker;
    private readonly DownloadManager _downloads;
    private readonly AssumedDownloadService _assumed;
    private readonly ManagerConfig _config;
    private readonly ModProfileService _profiles;

    public ModManagerController(
        LocalModService localMods,
        ModRepoService modRepo,
        ModMatchingService matching,
        VersionCheckerService versionChecker,
        DownloadManager downloads,
        AssumedDownloadService assumed,
        ManagerConfig config,
        ModProfileService profiles,
        IConfiguration appConfig)
    {
        _localMods = localMods;
        _modRepo = modRepo;
        _matching = matching;
        _versionChecker = versionChecker;
        _downloads = downloads;
        _assumed = assumed;
        _config = config;
        _profiles = profiles;

        var dataPath = appConfig["ResolvedDataPath"] ?? "../../data";
        _configPath = Path.Combine(dataPath, "manager-config.json");
    }

    // --- Folder Utilities ---

    // Returns whether the given folder path exists on disk.
    [HttpGet("check-folder")]
    public IActionResult CheckFolder([FromQuery] string path)
    {
        var exists = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        return Ok(new { exists });
    }

    // --- Game Launcher ---

    // Checks whether starsector.exe exists one folder above the configured mods path.
    [HttpGet("game-exe")]
    public IActionResult CheckGameExe()
    {
        var modsPath = _config.ModsPath;
        if (string.IsNullOrWhiteSpace(modsPath))
            return Ok(new { exists = false, exePath = (string?)null });

        var gameDir = Path.GetDirectoryName(modsPath);
        if (string.IsNullOrWhiteSpace(gameDir))
            return Ok(new { exists = false, exePath = (string?)null });

        var exePath = Path.Combine(gameDir, "starsector.exe");
        var exists = System.IO.File.Exists(exePath);
        return Ok(new { exists, exePath = exists ? exePath : null });
    }

    // Launches starsector.exe from the parent folder of the configured mods path.
    [HttpPost("launch-game")]
    public IActionResult LaunchGame()
    {
        var modsPath = _config.ModsPath;
        if (string.IsNullOrWhiteSpace(modsPath))
            return BadRequest(new { error = "Mods path is not configured" });

        var gameDir = Path.GetDirectoryName(modsPath);
        if (string.IsNullOrWhiteSpace(gameDir))
            return BadRequest(new { error = "Could not determine game directory" });

        var exePath = Path.Combine(gameDir, "starsector.exe");
        if (!System.IO.File.Exists(exePath))
            return NotFound(new { error = "starsector.exe not found" });

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = gameDir,
                UseShellExecute = true
            });
            return Ok(new { message = "Game launched" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to launch: {ex.Message}" });
        }
    }

    // --- Config ---

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(_config);
    }

    // Saves the user's mods path; marks the config as explicitly user-configured to suppress the first-run welcome prompt.
    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] ManagerConfig updated)
    {
        if (!string.IsNullOrWhiteSpace(updated.ModsPath))
            _config.ModsPath = updated.ModsPath;

        _config.IsUserConfigured = true;

        await PersistConfig();
        return Ok(_config);
    }

    // --- Local Mods ---

    [HttpGet("local-mods")]
    public IActionResult GetLocalMods()
    {
        return Ok(_localMods.GetCachedMods());
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan()
    {
        var mods = await _localMods.ScanAsync();
        await _profiles.CleanupMissingModsAsync(mods.Select(m => m.ModId).ToList());
        return Ok(new { count = mods.Count, mods });
    }

    // --- ModRepo ---

    [HttpPost("mod-repo/refresh")]
    public async Task<IActionResult> RefreshModRepo()
    {
        var data = await _modRepo.GetDataAsync(forceRefresh: true);
        return Ok(new { count = data.Items.Count, lastUpdated = data.LastUpdated });
    }

    // --- Version Checking ---

    [HttpPost("check-updates")]
    public async Task<IActionResult> CheckUpdates([FromQuery] bool force = false)
    {
        var results = await _versionChecker.CheckAllAsync(force);
        var updates = results.Values.Where(r => r.UpdateAvailable).ToList();
        return Ok(new
        {
            totalChecked = results.Count,
            updatesAvailable = updates.Count,
            updates = updates.Select(u => new
            {
                u.ModId,
                localVersion = u.LocalVersion?.ToString(),
                remoteVersion = u.RemoteVersion?.ToString(),
                u.DirectDownloadUrl
            })
        });
    }

    // --- Downloads ---

    [HttpGet("downloads")]
    public IActionResult GetDownloads()
    {
        return Ok(_downloads.GetAll());
    }

    [HttpPost("download")]
    public IActionResult StartDownload([FromBody] DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { error = "URL is required" });

        var item = _downloads.Enqueue(
            request.Url,
            request.ModName ?? "Unknown Mod",
            request.TopicId,
            request.GameVersion,
            request.PreviousGameVersion,
            request.ModVersion,
            request.PreviousModVersion);

        return Ok(item);
    }

    [HttpDelete("downloads/{id}")]
    public IActionResult CancelDownload(string id)
    {
        return _downloads.Cancel(id)
            ? Ok(new { message = "Canceled" })
            : NotFound(new { error = "Download not found or already finished" });
    }

    // Extracts and installs archive files that already exist in the configured mods folder.
    [HttpPost("extract-unextracted")]
    public async Task<IActionResult> ExtractUnextractedArchives()
    {
        await _localMods.EnsureScannedAsync();
        var result = await _downloads.ExtractUnextractedArchivesAsync();
        return Ok(new
        {
            extracted = result.Extracted,
            skipped = result.Skipped,
            failed = result.Failed,
            extractedArchives = result.ExtractedArchives,
            skippedArchives = result.SkippedArchives,
            failedArchives = result.FailedArchives,
            installedModIds = result.InstalledModIds
        });
    }

    // --- Enable/Disable ---

    // Enables a mod by adding its id to enabled_mods.json only.
    [HttpPost("enable/{modId}")]
    public async Task<IActionResult> EnableMod(string modId)
    {
        var mod = _localMods.GetByModIdIndex().GetValueOrDefault(modId);
        if (mod == null)
            return NotFound(new { error = $"Mod '{modId}' not found locally" });

        if (mod.IsEnabled)
            return Ok(new { message = "Already enabled" });

        // Update enabled_mods.json
        await UpdateEnabledModsJson(modId, enable: true);

        await _localMods.ScanAsync();
        return Ok(new { message = $"Mod '{modId}' enabled" });
    }

    // Disables a mod by removing its id from enabled_mods.json only.
    [HttpPost("disable/{modId}")]
    public async Task<IActionResult> DisableMod(string modId)
    {
        var mod = _localMods.GetByModIdIndex().GetValueOrDefault(modId);
        if (mod == null)
            return NotFound(new { error = $"Mod '{modId}' not found locally" });

        if (!mod.IsEnabled)
            return Ok(new { message = "Already disabled" });

        await UpdateEnabledModsJson(modId, enable: false);

        await _localMods.ScanAsync();
        return Ok(new { message = $"Mod '{modId}' disabled" });
    }

    // --- Delete ---

    [HttpDelete("mods/{modId}")]
    public async Task<IActionResult> DeleteMod(string modId)
    {
        var mod = _localMods.GetByModIdIndex().GetValueOrDefault(modId);
        if (mod == null)
            return NotFound(new { error = $"Mod '{modId}' not found locally" });

        try
        {
            Directory.Delete(mod.FolderPath, recursive: true);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete: {ex.Message}" });
        }

        await UpdateEnabledModsJson(modId, enable: false);
        await _profiles.RemoveModFromAllProfilesAsync(modId);
        await _localMods.ScanAsync();
        return Ok(new { message = $"Mod '{modId}' deleted" });
    }

    // --- Helpers ---

    private async Task UpdateEnabledModsJson(string modId, bool enable)
    {
        var modsPath = _config.ModsPath;
        var enabledModsPath = Path.Combine(modsPath, "enabled_mods.json");

        try
        {
            EnabledModsFile? data = null;
            if (System.IO.File.Exists(enabledModsPath))
            {
                var json = await System.IO.File.ReadAllTextAsync(enabledModsPath);
                data = JsonSerializer.Deserialize<EnabledModsFile>(json, JsonOpts);
            }
            data ??= new EnabledModsFile();

            if (enable)
            {
                if (!data.EnabledMods.Contains(modId, StringComparer.OrdinalIgnoreCase))
                    data.EnabledMods.Add(modId);
            }
            else
            {
                data.EnabledMods.RemoveAll(id => id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            }

            var output = JsonSerializer.Serialize(data, JsonOpts);
            await System.IO.File.WriteAllTextAsync(enabledModsPath, output);
        }
        catch (Exception)
        {
            // Non-fatal: enabled_mods.json sync failure
        }
    }

    private async Task PersistConfig()
    {
        var json = JsonSerializer.Serialize(_config, JsonOpts);
        await System.IO.File.WriteAllTextAsync(_configPath, json);
    }

    // --- Assumed Downloads ---

    [HttpPost("resolve-assumed-download")]
    public async Task<IActionResult> ResolveAssumedDownload([FromBody] ResolveAssumedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { error = "URL is required" });

        try
        {
            var resolved = await _assumed.ResolveSingleUrlAsync(request.Url);
            return Ok(new { resolvedUrl = resolved ?? request.Url, originalUrl = request.Url });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

// Request body for the resolve-assumed-download endpoint, identifying the URL and its forum topic.
public class ResolveAssumedRequest
{
    public string Url { get; set; } = "";
    public int? TopicId { get; set; }
}

// Request body for the start-download endpoint with optional version metadata for delta tracking.
public class DownloadRequest
{
    public string Url { get; set; } = "";
    public string? ModName { get; set; }
    public int? TopicId { get; set; }
    public string? GameVersion { get; set; }
    public string? PreviousGameVersion { get; set; }
    public string? ModVersion { get; set; }
    public string? PreviousModVersion { get; set; }
}

// Mirrors the enabled_mods.json structure used by Starsector to track active mods.
public class EnabledModsFile
{
    public List<string> EnabledMods { get; set; } = [];
}


