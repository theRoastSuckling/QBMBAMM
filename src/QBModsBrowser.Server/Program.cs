using System.Text.Json;
using QBModsBrowser.Scraper.Storage;
using QBModsBrowser.Server.Models;
using QBModsBrowser.Server.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Threading;

const string SingleInstanceMutexName = @"Local\QBModsBrowser.Server.SingleInstance";
using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out bool isPrimaryInstance);

if (!isPrimaryInstance)
{
    Console.WriteLine("QBModsBrowser.Server is already running. Close the other instance, then build or run again.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Resolve paths relative to workspace root (two levels up from the server project)
string basePath = Path.GetFullPath(
    builder.Configuration["DataPath"] ?? "../../data",
    builder.Environment.ContentRootPath);
string logPath = Path.GetFullPath(
    builder.Configuration["LogPath"] ?? "../../logs",
    builder.Environment.ContentRootPath);

Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logPath);

// Runtime switch that lets the UI toggle INF console output without restarting.
var consoleLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);

// Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Keep framework logs visible overall, but quiet down noisy MVC internals.
    .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(levelSwitch: consoleLevelSwitch)
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(logPath, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(logPath, "scraper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
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

// Override from appsettings if present and config file didn't have a custom value
var appSettingsModsPath = builder.Configuration["ModsPath"];
if (!string.IsNullOrWhiteSpace(appSettingsModsPath)
    && string.Equals(managerConfig.ModsPath, ManagerConfig.DefaultModsPath, StringComparison.Ordinal))
    managerConfig.ModsPath = appSettingsModsPath;

// Store resolved data path so controllers can find it
builder.Configuration["ResolvedDataPath"] = basePath;

// Services
builder.Services.AddSingleton(consoleLevelSwitch);
builder.Services.AddSingleton(new JsonDataStore(Log.Logger, basePath));
builder.Services.AddSingleton<ScraperOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScraperOrchestrator>());
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton(managerConfig);
builder.Services.AddSingleton(new ModRepoService(Log.Logger, basePath));
builder.Services.AddSingleton(new LocalModService(Log.Logger, () => managerConfig.ModsPath));
builder.Services.AddSingleton(sp => new VersionCheckerService(Log.Logger, sp.GetRequiredService<LocalModService>()));
builder.Services.AddSingleton(new ModInstallationService(Log.Logger));
builder.Services.AddSingleton(new AssumedDownloadService(Log.Logger, basePath));
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

app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// Fallback to index.html for SPA-like navigation
app.MapFallbackToFile("index.html");

// Apply saved console log-level preference before the first log line.
var savedScraperConfig = await app.Services.GetRequiredService<JsonDataStore>().LoadConfig();
consoleLevelSwitch.MinimumLevel = savedScraperConfig.ShowInfoConsoleLogs
    ? LogEventLevel.Information
    : LogEventLevel.Warning;

Log.Information("Server starting. Data: {DataPath}, Logs: {LogPath}, ModsPath: {ModsPath}", basePath, logPath, managerConfig.ModsPath);

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
    string url = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        Log.Information("Opened browser at {Url}", url);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not auto-open browser");
    }
});

app.Run();
