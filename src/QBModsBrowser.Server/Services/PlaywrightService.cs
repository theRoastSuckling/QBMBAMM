using System.Text;
using Microsoft.Playwright;
using ILogger = Serilog.ILogger;

namespace QBModsBrowser.Server.Services;

// Detects whether Playwright Chromium is installed and can stream a live installation.
// Playwright installs browsers to %LOCALAPPDATA%\ms-playwright so they persist across app updates.
public class PlaywrightService
{
    private readonly ILogger _log;
    private readonly object _lock = new();

    private readonly List<string> _installLines = [];
    private volatile bool _installRunning;
    private bool? _installSucceeded;
    private int _installExitCode = -1;
    // Set when a scrape fails because the required Chromium revision is missing (stale install after app update).
    private volatile bool _browserMissing;
    // Exact executable path that the current Playwright SDK version expects; resolved once at startup.
    private string? _knownExecutablePath;

    // Accepts a logger for recording install lifecycle events.
    public PlaywrightService(ILogger logger)
    {
        _log = logger.ForContext<PlaywrightService>();
    }

    // Resolves the exact Chromium executable path this Playwright version requires by briefly
    // creating a Playwright instance (driver only — no browser launched), caching the result.
    // Must be called once at startup so IsInstalled() can make a precise check from the first poll.
    public async Task InitializeAsync()
    {
        try
        {
            using var pw = await Playwright.CreateAsync();
            _knownExecutablePath = pw.Chromium.ExecutablePath;
            _log.Information("Playwright expected Chromium path: {Path}", _knownExecutablePath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not resolve Playwright executable path at startup — falling back to glob check");
        }
    }

    // Returns true only if the Chromium revision required by the current Playwright version is present.
    // When _knownExecutablePath is set (resolved at startup), checks the exact file; otherwise falls
    // back to a glob walk over chromium-* dirs. _browserMissing overrides both paths to false.
    public bool IsInstalled()
    {
        if (_browserMissing) return false;

        if (_knownExecutablePath != null)
            return File.Exists(_knownExecutablePath);

        // Fallback: any chromium-*/chrome.exe found under ms-playwright.
        var msPlaywrightDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");

        if (!Directory.Exists(msPlaywrightDir))
            return false;

        foreach (var chromiumDir in Directory.EnumerateDirectories(msPlaywrightDir, "chromium-*"))
        {
            if (Directory.EnumerateFiles(chromiumDir, "chrome.exe", SearchOption.AllDirectories).Any())
                return true;
        }

        return false;
    }

    // Returns a fine-grained status string for the UI:
    //   "installed"     — required Chromium binary is present and ready
    //   "outdated"      — an old chromium-* folder exists but the required revision is missing
    //   "not_installed" — no chromium-* folder found at all
    public string GetBrowserStatus()
    {
        if (IsInstalled()) return "installed";

        var msPlaywrightDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");

        if (Directory.Exists(msPlaywrightDir) &&
            Directory.EnumerateDirectories(msPlaywrightDir, "chromium-*").Any())
            return "outdated";

        return "not_installed";
    }

    // Called by the scraper when Playwright throws "Executable doesn't exist", meaning the required
    // Chromium revision is missing. Forces IsInstalled() to return false so the UI shows the install prompt.
    public void MarkBrowserMissing()
    {
        _browserMissing = true;
        _log.Warning("Playwright browser marked as missing — required Chromium revision not found");
    }

    // Kicks off a background Playwright Chromium install. Returns false if one is already running.
    public bool StartInstall()
    {
        lock (_lock)
        {
            if (_installRunning) return false;
            _installRunning = true;
            _installSucceeded = null;
            _installExitCode = -1;
            _installLines.Clear();
        }

        _ = Task.Run(() => RunInstall());
        return true;
    }

    // Returns a snapshot of the current installation state for the polling endpoint.
    public PlaywrightInstallStatus GetInstallStatus()
    {
        lock (_lock)
        {
            return new PlaywrightInstallStatus(
                Running: _installRunning,
                Succeeded: _installSucceeded,
                ExitCode: _installExitCode,
                Lines: [.. _installLines]);
        }
    }

    // Refreshes _knownExecutablePath by briefly creating a Playwright instance (no browser launch).
    // Called after a successful install so IsInstalled() immediately reflects the new revision.
    private async Task RefreshExecutablePathAsync()
    {
        try
        {
            using var pw = await Playwright.CreateAsync();
            _knownExecutablePath = pw.Chromium.ExecutablePath;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not refresh Playwright executable path after install");
        }
    }

    // Runs the playwright install command, emitting progress lines into the buffer.
    // Note: the Playwright CLI spawns a native subprocess whose output goes to the OS stdout pipe
    // (bypassing Console.SetOut). We capture Console.Out to catch any .NET-layer output and
    // filter out Serilog log lines (which share the same stream), adding our own status messages
    // at key points so the UI always shows meaningful feedback.
    private async Task RunInstall()
    {
        _log.Information("Starting Playwright Chromium installation");

        lock (_lock)
            _installLines.Add("Downloading Playwright Chromium (~300 MB), please wait…");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var capturing = new CapturingWriter(_installLines, _lock, originalOut);

        try
        {
            Console.SetOut(capturing);
            Console.SetError(capturing);

            _installExitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);

            // Clear the missing flag and refresh the cached executable path so IsInstalled()
            // uses the newly installed revision rather than the stale cached path.
            _browserMissing = false;
            await RefreshExecutablePathAsync();
            bool installed = IsInstalled();
            lock (_lock)
            {
                _installSucceeded = _installExitCode == 0 && installed;
                _installLines.Add(_installSucceeded.Value
                    ? "Playwright Chromium installed successfully."
                    : $"Installation finished with exit code {_installExitCode} but Chromium was not found.");
            }

            _log.Information("Playwright install finished with exit code {Code}, detected={Installed}",
                _installExitCode, installed);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Playwright install threw an exception");
            lock (_lock)
            {
                _installLines.Add($"Error: {ex.Message}");
                _installSucceeded = false;
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            _installRunning = false;
        }
    }

    // Snapshot returned to callers asking about install progress.
    public record PlaywrightInstallStatus(
        bool Running,
        bool? Succeeded,
        int ExitCode,
        List<string> Lines);

    // Captures console output lines while forwarding writes to the original stream.
    private sealed class CapturingWriter : TextWriter
    {
        private readonly List<string> _lines;
        private readonly object _lock;
        private readonly TextWriter _inner;

        public override Encoding Encoding => Encoding.UTF8;

        // Wraps an existing TextWriter, appending all output to lines under the given lock.
        internal CapturingWriter(List<string> lines, object syncLock, TextWriter inner)
        {
            _lines = lines;
            _lock = syncLock;
            _inner = inner;
        }

        // Appends a non-empty fragment unless it looks like a Serilog log line.
        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value) && !IsSerilogLine(value))
                lock (_lock) _lines.Add(value);

            _inner.Write(value);
        }

        // Appends a full line unless it looks like a Serilog log line (starts with [HH:MM:SS).
        public override void WriteLine(string? value)
        {
            if (!IsSerilogLine(value))
                lock (_lock) _lines.Add(value ?? string.Empty);
            _inner.WriteLine(value);
        }

        // Appends a blank line marker.
        public override void WriteLine()
        {
            lock (_lock) _lines.Add(string.Empty);
            _inner.WriteLine();
        }

        // Serilog console output starts with a bracketed timestamp: [HH:MM:SS
        private static bool IsSerilogLine(string? value) =>
            value != null &&
            value.Length > 9 &&
            value[0] == '[' &&
            char.IsDigit(value[1]) &&
            char.IsDigit(value[2]) &&
            value[3] == ':';
    }
}
