using System.Diagnostics;
using System.Text.Json;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Utilities;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Serializes a ForumDataBundle to the local QBForumModData repo clone and pushes it to GitHub.
public class ForumDataPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = FormatHelper.IndentedCamelCase;
    private const string BundleFileName = "forum-data-bundle.json";

    private readonly ILogger _log;
    private readonly ForumDataRepoConfig _config;

    // Accepts the repo config so the publish path can be resolved without re-reading app-config.
    public ForumDataPublisher(ILogger logger, ForumDataRepoConfig config)
    {
        _log = logger.ForContext<ForumDataPublisher>();
        _config = config;
    }

    // Writes the bundle JSON to LocalRepoPath and runs git add/commit/push.
    // No-ops silently when LocalRepoPath is not configured (regular client machines).
    public async Task PublishAsync(ForumDataBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(_config.LocalRepoPath))
        {
            _log.Debug("Forum data publishing skipped: LocalRepoPath not configured");
            return;
        }

        if (!Directory.Exists(_config.LocalRepoPath))
        {
            _log.Warning("Forum data publishing skipped: LocalRepoPath does not exist ({Path})", _config.LocalRepoPath);
            return;
        }

        var bundlePath = Path.Combine(_config.LocalRepoPath, BundleFileName);

        _log.Information("Publishing forum data bundle to {Path}", bundlePath);

        try
        {
            var json = JsonSerializer.Serialize(bundle, JsonOpts);
            await File.WriteAllTextAsync(bundlePath, json);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to write forum-data-bundle.json to {Path}", bundlePath);
            return;
        }

        var commitMessage = $"scrape update: {bundle.UpdatedAt:u}";

        await RunGitAsync("add", BundleFileName);
        var committed = await RunGitAsync("commit", $"-m \"{commitMessage}\"");

        // Nothing to commit (bundle unchanged) — skip push.
        if (!committed)
        {
            _log.Information("Forum data bundle unchanged since last publish, skipping push");
            return;
        }

        await RunGitAsync("push");
        _log.Information("Forum data bundle pushed ({UpdatedAt:u})", bundle.UpdatedAt);
    }

    // Runs a git command inside LocalRepoPath. Returns true when the process exits with code 0.
    private async Task<bool> RunGitAsync(string command, string? args = null)
    {
        var fullArgs = $"-C \"{_config.LocalRepoPath}\" {command}";
        if (!string.IsNullOrWhiteSpace(args))
            fullArgs += $" {args}";

        var psi = new ProcessStartInfo("git", fullArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            _log.Error("Failed to start git process (command: git {FullArgs})", fullArgs);
            return false;
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            // "nothing to commit" is not a real error.
            if (command == "commit" && stderr.Contains("nothing to commit"))
                return false;

            _log.Warning("git {Command} exited {Code}: {Stderr}", command, proc.ExitCode, stderr.Trim());
            return false;
        }

        if (!string.IsNullOrWhiteSpace(stdout))
            _log.Debug("git {Command}: {Stdout}", command, stdout.Trim());

        return true;
    }
}
