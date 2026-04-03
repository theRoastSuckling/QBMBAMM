param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("minor", "patch")]
    [string]$BumpType
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

$indexHtmlPath = Join-Path $repoRoot "src\QBModsBrowser.Server\wwwroot\index.html"
$projectPath = Join-Path $repoRoot "src\QBModsBrowser.Server\QBModsBrowser.Server.csproj"
$publishRoot = Join-Path $repoRoot "publish"
$runtime = "win-x64"
$publishRuntimeRoot = Join-Path $publishRoot $runtime
$shortcutPath = Join-Path $repoRoot "QBModsBrowser.Server.lnk"
$sourceDataPath = Join-Path $repoRoot "data"

if (-not (Test-Path -LiteralPath $indexHtmlPath)) {
    throw "Version file not found: $indexHtmlPath"
}
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

$html = Get-Content -LiteralPath $indexHtmlPath -Raw -Encoding UTF8
$versionMatch = [regex]::Match($html, '(<span class="text-xs text-indigo-200/80 font-mono">)v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(</span>)')
if (-not $versionMatch.Success) {
    throw "Could not find the version span in index.html (expected like <span ...>v1.0.0</span>)."
}

$major = [int]$versionMatch.Groups["major"].Value
$minor = [int]$versionMatch.Groups["minor"].Value
$patch = [int]$versionMatch.Groups["patch"].Value

switch ($BumpType) {
    "minor" {
        $minor += 1
        $patch = 0
    }
    "patch" {
        $patch += 1
    }
}

$oldVersion = "v{0}.{1}.{2}" -f $versionMatch.Groups["major"].Value, $versionMatch.Groups["minor"].Value, $versionMatch.Groups["patch"].Value
$newVersion = "v{0}.{1}.{2}" -f $major, $minor, $patch
$publishDir = Join-Path $publishRuntimeRoot $newVersion
# Zip name includes version so each release produces a distinct, identifiable archive.
$zipPath = Join-Path $repoRoot "QBMBAMM-win-x64-$newVersion.zip"
$updatedHtml = [regex]::Replace(
    $html,
    [regex]::Escape($oldVersion),
    $newVersion,
    1
)
Set-Content -LiteralPath $indexHtmlPath -Value $updatedHtml -Encoding UTF8

Write-Host "Version bumped: $oldVersion -> $newVersion"
Write-Host "Publishing single-file executable..."

$running = Get-Process -Name "QBMBAMM" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running QBModsBrowser.Server process..."
    $running | Stop-Process -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

dotnet publish $projectPath `
    -c Release `
    -r $runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $publishDir "QBMBAMM.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish completed but executable not found at: $exePath"
}

# Make packaged release self-contained for user data/log paths.
$publishedSettingsPath = Join-Path $publishDir "appsettings.json"
if (Test-Path -LiteralPath $publishedSettingsPath) {
    $settings = Get-Content -LiteralPath $publishedSettingsPath -Raw | ConvertFrom-Json
    $settings.DataPath = "./data"
    $settings.LogPath = "./logs"
    # Clear developer-specific mods path so new users are prompted to set their own.
    $settings.ModsPath = ""
    ($settings | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $publishedSettingsPath -Encoding UTF8
}

# Copy app-config.json next to the exe so published builds can locate it (Program.cs probes AppContext.BaseDirectory first).
$sourceAppConfigPath = Join-Path $repoRoot "app-config.json"
if (Test-Path -LiteralPath $sourceAppConfigPath) {
    Copy-Item -LiteralPath $sourceAppConfigPath -Destination (Join-Path $publishDir "app-config.json") -Force
}

# Seed release with current data snapshot, excluding browser profile/cache.
if (Test-Path -LiteralPath $sourceDataPath) {
    $publishDataPath = Join-Path $publishDir "data"
    if (Test-Path -LiteralPath $publishDataPath) {
        Remove-Item -LiteralPath $publishDataPath -Recurse -Force
    }
    Copy-Item -LiteralPath $sourceDataPath -Destination $publishDataPath -Recurse -Force

    $publishBrowserProfilePath = Join-Path $publishDataPath "browser-profile"
    if (Test-Path -LiteralPath $publishBrowserProfilePath) {
        Remove-Item -LiteralPath $publishBrowserProfilePath -Recurse -Force
    }

    # Exclude cached external images; users fetch their own on first run.
    $publishExternalImagesPath = Join-Path $publishDataPath "external-images"
    if (Test-Path -LiteralPath $publishExternalImagesPath) {
        Remove-Item -LiteralPath $publishExternalImagesPath -Recurse -Force
    }

    # Exclude user-specific mod profile lists.
    $publishProfilesPath = Join-Path $publishDataPath "mod-profiles.json"
    if (Test-Path -LiteralPath $publishProfilesPath) {
        Remove-Item -LiteralPath $publishProfilesPath -Force
    }

    # Exclude user-specific manager config (contains personal modsPath); regenerated on first run.
    $publishManagerConfigPath = Join-Path $publishDataPath "manager-config.json"
    if (Test-Path -LiteralPath $publishManagerConfigPath) {
        Remove-Item -LiteralPath $publishManagerConfigPath -Force
    }
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $publishDir
$shortcut.IconLocation = $exePath
$shortcut.Description = "QBModsBrowser Server"
$shortcut.Save()

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Done."
Write-Host "Version: $newVersion"
Write-Host "Executable: $exePath"
Write-Host "Shortcut: $shortcutPath"
Write-Host "Zip: $zipPath"
if (Test-Path -LiteralPath $sourceDataPath) {
    Write-Host "Bundled data: $sourceDataPath (excluding browser-profile, external-images)"
}
