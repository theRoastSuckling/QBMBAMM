using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Serilog;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Shared helper for reading this exe's version from AssemblyInformationalVersion.
// Strips the "+git-hash" suffix MSBuild appends. Used by Program.cs (startup takeover) and AppUpdateService.
public static class AppVersion
{
    public static Version GetCurrent()
    {
        var raw = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0.0.0";
        var clean = raw.Contains('+') ? raw[..raw.IndexOf('+')] : raw;
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
    }

    public static string GetCurrentString() => "v" + GetCurrent().ToString(3);
}

// DTO returned from GET /api/app-update/status.
public record AppUpdateStatus(
    string CurrentVersion,
    string? RemoteVersion,
    bool UpdateAvailable,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string? AssetUrl,
    DateTimeOffset? LastCheckedAt,
    string? LastError);

// Checks GitHub for the latest release, downloads the ZIP asset, extracts it, and spawns a batch
// script that copies the extracted files over the running install folder after this process exits.
public class AppUpdateService
{
    const string GitHubApiUrl = "https://api.github.com/repos/theRoastSuckling/QBMBAMM/releases/latest";
    const string AssetFileName = "QBMBAMM-Windows.zip";
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    // Minimal updater: waits for the old PID to exit, robocopy /E overwrites files from SRC into DEST
    // (no /MIR so runtime-generated data/ and logs/ folders are preserved), relaunches, cleans up, self-deletes.
    const string UpdateBatTemplate = @"@echo off
setlocal
set PID=%~1
set SRC=%~2
set DEST=%~3
:wait
tasklist /FI ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)
robocopy ""%SRC%"" ""%DEST%"" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NC /NS /NP
start """" ""%DEST%\QBMBAMM.exe""
rmdir /s /q ""%SRC%""
(goto) 2>nul & del ""%~f0""
";

    readonly ILogger _log;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    AppUpdateStatus? _cached;
    DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    bool _installing;

    public AppUpdateService(ILogger log)
    {
        _log = log.ForContext<AppUpdateService>();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub rejects requests without a User-Agent; accept-header pins the v3 JSON schema.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QBMBAMM-Updater");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // Returns cached status if fresh (<60s), otherwise re-queries GitHub. Errors are captured in LastError so the UI can still render.
    public async Task<AppUpdateStatus> GetStatusAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_cached != null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var current = AppVersion.GetCurrent();
            try
            {
                using var resp = await _http.GetAsync(GitHubApiUrl);
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? "";
                var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                string? assetUrl = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.Equals(name, AssetFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            assetUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                            break;
                        }
                    }
                }

                var remote = ParseTagVersion(tag);
                var updateAvailable = remote != null && remote > current && assetUrl != null;
                _cached = new AppUpdateStatus(
                    CurrentVersion: "v" + current.ToString(3),
                    RemoteVersion: remote != null ? "v" + remote.ToString(3) : tag,
                    UpdateAvailable: updateAvailable,
                    ReleaseNotes: body,
                    ReleaseUrl: htmlUrl,
                    AssetUrl: assetUrl,
                    LastCheckedAt: DateTimeOffset.UtcNow,
                    LastError: null);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "GitHub release check failed");
                _cached = new AppUpdateStatus(
                    CurrentVersion: "v" + current.ToString(3),
                    RemoteVersion: null,
                    UpdateAvailable: false,
                    ReleaseNotes: null,
                    ReleaseUrl: null,
                    AssetUrl: null,
                    LastCheckedAt: DateTimeOffset.UtcNow,
                    LastError: ex.Message);
            }
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached!;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Downloads the release ZIP, extracts it to a temp folder, writes update.bat, spawns it detached,
    // and schedules this process to exit so the bat can overwrite files.
    public async Task<bool> DownloadAndInstallAsync()
    {
        if (_installing) return false;
        _installing = true;
        try
        {
            var status = await GetStatusAsync();
            if (!status.UpdateAvailable || string.IsNullOrEmpty(status.AssetUrl))
            {
                _log.Warning("Install requested but no update is available");
                return false;
            }

            var installDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new InvalidOperationException("Could not resolve install directory from Environment.ProcessPath");

            var versionTag = (status.RemoteVersion ?? "next").TrimStart('v');
            var tempRoot = Path.Combine(Path.GetTempPath(), $"QBMBAMM-update-{versionTag}-{Guid.NewGuid():N}");
            var zipPath = tempRoot + ".zip";
            var extractDir = tempRoot;

            _log.Information("Downloading update {Url} to {Zip}", status.AssetUrl, zipPath);
            using (var dl = await _http.GetAsync(status.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                dl.EnsureSuccessStatusCode();
                await using var src = await dl.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                await src.CopyToAsync(dst);
            }

            _log.Information("Extracting update to {Dir}", extractDir);
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }

            var batPath = Path.Combine(Path.GetTempPath(), $"QBMBAMM-update-{versionTag}-{Guid.NewGuid():N}.bat");
            await File.WriteAllTextAsync(batPath, UpdateBatTemplate);

            var pid = Environment.ProcessId;
            _log.Information("Spawning updater: {Bat} PID={Pid} SRC={Src} DEST={Dest}", batPath, pid, extractDir, installDir);
            // UseShellExecute=true launches via Windows ShellExecute: the bat runs detached in its own
            // cmd.exe window, arguments are passed correctly even when paths contain spaces,
            // and the parent process does not wait for it to finish.
            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                Arguments = $"{pid} \"{extractDir}\" \"{installDir}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });

            // Same pattern as AppController.Shutdown: let the HTTP response return, then exit the WinForms message loop
            // so Program.cs can clean up the tray icon. The bat is already waiting for this PID to exit.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Application.Exit();
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Auto-update install failed");
            _installing = false;
            throw;
        }
    }

    // Accepts tags like "r2.2.14", "v2.2.14", "2.2.14".
    static Version? ParseTagVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'r' || s[0] == 'R' || s[0] == 'v' || s[0] == 'V')) s = s[1..];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
