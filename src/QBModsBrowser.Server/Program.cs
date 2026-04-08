using System.Text.Json;
using System.Windows.Forms;
using System.Threading;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;
using QBModsBrowser.Server.Utilities;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Entry point for the system-tray-hosted ASP.NET Core server.
// [STAThread] is required for Windows Forms (NotifyIcon/ContextMenuStrip).
internal static class Program
{
    // Fixed listen URL; single-instance check and tray icon both use this.
    const string AppUrl = "http://localhost:5000";
    // When started via QBMBAMM.exe from bin/.../net9.0-windows, BaseDirectory is that folder; use the project
    // directory as content root so appsettings paths (e.g. ../../data) match `dotnet run`. Published layouts have no .csproj nearby, so we keep BaseDirectory.
    static string ResolveWebContentRoot()
    {
        var start = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QBModsBrowser.Server.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return start;
    }

    [STAThread]
    static async Task Main(string[] args)
    {
        // Bootstrap Windows Forms before any UI objects are created.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        const string SingleInstanceMutexName = @"Local\QBModsBrowser.Server.SingleInstance";
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out bool isPrimaryInstance);

        if (!isPrimaryInstance)
        {
            // Another instance is already running — just (re)open the browser instead of nagging the user.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppUrl) { UseShellExecute = true });
            return;
        }

        var contentRoot = ResolveWebContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
        });
        builder.WebHost.UseUrls(AppUrl);

        // In dev, contentRoot is the project dir so data lives two levels up (repo root).
        // In published builds, contentRoot equals AppContext.BaseDirectory so data is next to the exe.
        bool isPublishedBuild = Path.GetFullPath(contentRoot).Equals(
            Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        string basePath = Path.GetFullPath(
            builder.Configuration["DataPath"] ?? (isPublishedBuild ? "data" : "../../data"),
            contentRoot);
        string logPath = Path.GetFullPath(
            builder.Configuration["LogPath"] ?? (isPublishedBuild ? "logs" : "../../logs"),
            contentRoot);

        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(logPath);

        // Serilog — console always receives INF and above.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Keep framework logs visible overall, but quiet down noisy MVC internals.
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
            // Drop "Executing/Executed endpoint" routing events for the high-frequency polling endpoints.
            .Filter.ByExcluding(le =>
                !LogOptions.ShowPollingLogs &&
                le.Properties.TryGetValue("EndpointName", out var en) &&
                (en.ToString().Contains("GetStatus") || en.ToString().Contains("GetLogs")))
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(logPath, "server-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            // Scraper log: restrict to QBModsBrowser sources so framework HTTP noise stays out.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(le =>
                    !le.Properties.TryGetValue("SourceContext", out var sc) ||
                    sc.ToString().StartsWith("\"QBModsBrowser"))
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    Path.Combine(logPath, "scraper-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30))
            .CreateLogger();

        builder.Host.UseSerilog();

        // Resolve app-config.json: next to the exe for published builds, or two levels up for dev.
        var appConfigPath =
            File.Exists(Path.Combine(AppContext.BaseDirectory, "app-config.json"))
                ? Path.Combine(AppContext.BaseDirectory, "app-config.json")
                : Path.GetFullPath("../../app-config.json", builder.Environment.ContentRootPath);
        var appConfig = new AppConfig();
        if (File.Exists(appConfigPath))
        {
            try
            {
                var acJson = File.ReadAllText(appConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(acJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) appConfig = loaded;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load app-config.json, using defaults");
            }
        }

        // Resolve a relative LocalRepoPath against the directory that contains app-config.json.
        var repoPath = appConfig.ForumDataRepo.LocalRepoPath;
        if (!string.IsNullOrWhiteSpace(repoPath) && !Path.IsPathRooted(repoPath))
            appConfig.ForumDataRepo.LocalRepoPath =
                Path.GetFullPath(repoPath, Path.GetDirectoryName(appConfigPath)!);

        // Load or create manager config
        var managerConfigPath = Path.Combine(basePath, "manager-config.json");
        var managerConfig = new ManagerConfig();
        if (File.Exists(managerConfigPath))
        {
            try
            {
                var mcJson = File.ReadAllText(managerConfigPath);
                var loaded = JsonSerializer.Deserialize<ManagerConfig>(mcJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) managerConfig = loaded;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load manager-config.json, using defaults");
            }
        }

        // Older saves used an empty mods path; normalize so local scan and UI get a sensible default.
        if (string.IsNullOrWhiteSpace(managerConfig.ModsPath))
            managerConfig.ModsPath = ManagerConfig.DefaultModsPath;

        // Migration: treat existing config files that have a real path as user-configured so the welcome prompt is not shown again.
        if (File.Exists(managerConfigPath) && !managerConfig.IsUserConfigured)
            managerConfig.IsUserConfigured = true;

        // Store resolved paths so controllers can find them without re-resolving relative to CWD.
        builder.Configuration["ResolvedDataPath"] = basePath;
        builder.Configuration["ResolvedLogPath"] = logPath;

        Log.Information("Server starting. Data: {DataPath}, Logs: {LogPath}, ModsPath: {ModsPath}", basePath, logPath, managerConfig.ModsPath);

        // Services
        builder.Services.AddSingleton(new PlaywrightService(Log.Logger));
        builder.Services.AddSingleton(new JsonDataStore(Log.Logger, basePath));
        builder.Services.AddSingleton(new ForumDataBundler(Log.Logger));
        builder.Services.AddSingleton(new ForumDataPublisher(Log.Logger, appConfig.ForumDataRepo));
        builder.Services.AddSingleton(sp => new ScraperOrchestrator(
            sp.GetRequiredService<JsonDataStore>(),
            sp.GetRequiredService<AssumedDownloadService>(),
            sp.GetRequiredService<ForumDataBundler>(),
            sp.GetRequiredService<ForumDataPublisher>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScraperOrchestrator>());
        builder.Services.AddSingleton(appConfig);
        builder.Services.AddSingleton(managerConfig);
        builder.Services.AddSingleton(new ModRepoService(Log.Logger, basePath));
        builder.Services.AddSingleton(new LocalModService(Log.Logger, () => managerConfig.ModsPath));
        builder.Services.AddSingleton(sp => new VersionCheckerService(Log.Logger, sp.GetRequiredService<LocalModService>()));
        builder.Services.AddSingleton(new ModInstallationService(Log.Logger));
        builder.Services.AddSingleton(new AssumedDownloadService(Log.Logger, basePath));
        builder.Services.AddSingleton(sp => new ForumDataFetchService(
            Log.Logger,
            appConfig.ForumDataRepo,
            basePath,
            sp.GetRequiredService<JsonDataStore>(),
            sp.GetRequiredService<ForumDataBundler>(),
            sp.GetRequiredService<AssumedDownloadService>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ForumDataFetchService>());
        builder.Services.AddSingleton(sp => new DownloadManager(
            Log.Logger,
            () => managerConfig.ModsPath,
            sp.GetRequiredService<ModInstallationService>(),
            sp.GetRequiredService<LocalModService>(),
            sp.GetRequiredService<VersionCheckerService>(),
            sp.GetRequiredService<AssumedDownloadService>(),
            basePath));
        builder.Services.AddSingleton(sp => new ModMatchingService(
            Log.Logger,
            sp.GetRequiredService<JsonDataStore>(),
            sp.GetRequiredService<ModRepoService>(),
            sp.GetRequiredService<LocalModService>(),
            sp.GetRequiredService<VersionCheckerService>(),
            sp.GetRequiredService<DownloadManager>(),
            basePath));
        builder.Services.AddSingleton(sp => new ModProfileService(
            Log.Logger, basePath, () => managerConfig.ModsPath,
            sp.GetRequiredService<LocalModService>()));
        builder.Services.AddSingleton(sp => new DependencyService(
            sp.GetRequiredService<LocalModService>(),
            sp.GetRequiredService<ModMatchingService>(),
            sp.GetRequiredService<ModRepoService>()));
        builder.Services.AddControllers();

        var app = builder.Build();

        // Suppress HTTP request log entries for the high-frequency polling endpoints unless opted in.
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (ctx, _, _) =>
            {
                if (!LogOptions.ShowPollingLogs)
                {
                    var path = ctx.Request.Path.Value ?? "";
                    if (path.Equals("/api/scraper/status", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/scraper/logs", StringComparison.OrdinalIgnoreCase))
                        return LogEventLevel.Verbose;
                }
                return LogEventLevel.Information;
            };
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapControllers();

        // Fallback to index.html for SPA-like navigation
        app.MapFallbackToFile("index.html");

        // Build the system tray icon; it lives until the user clicks Exit.
        using var tray = new TrayApp(AppUrl);

        // Initialize mod manager services in background, then warm version-checker cache.
        _ = Task.Run(async () =>
        {
            try
            {
                var matching = app.Services.GetRequiredService<ModMatchingService>();
                await matching.InitializeAsync();
                Log.Information("Mod manager initialized");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Mod manager initialization failed (non-fatal)");
            }

            // Warm version-check cache on every startup so data is available without a manual "check for updates".
            // Runs after InitializeAsync so local-mod scan is complete before CheckAllAsync reads GetCachedMods().
            try
            {
                var versionChecker = app.Services.GetRequiredService<VersionCheckerService>();
                await versionChecker.CheckAllAsync(forceRefresh: false);
                Log.Information("Startup version check complete");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Startup version check failed (non-fatal)");
            }
        });

        // Auto-open browser when server starts
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppUrl) { UseShellExecute = true });
                Log.Information("Opened browser at {Url}", AppUrl);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not auto-open browser");
            }
        });

        // Start the web host without blocking; the WinForms pump below keeps the process alive.
        await app.StartAsync();

        // Block on the WinForms message loop; returns when the user clicks Exit.
        tray.Run();

        // Clear the WinForms sync context before shutdown: Application.Run() has already returned so
        // the message pump is dead, but WindowsFormsSynchronizationContext is still installed on
        // this STA thread. If we leave it set, any 'await' continuation inside StopAsync() will
        // be posted to the dead pump and never execute, causing the process to hang indefinitely.
        SynchronizationContext.SetSynchronizationContext(null);

        // Graceful shutdown after the user exits the tray.
        await app.StopAsync();
        Log.CloseAndFlush();
    }
}
