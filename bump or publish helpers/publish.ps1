$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# --- Paths and constants ---
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

$projectPath = Join-Path $repoRoot "src\QBModsBrowser.Server\QBModsBrowser.Server.csproj"
$publishRoot = Join-Path $repoRoot "publish"
$runtime = "win-x64"
$publishRuntimeRoot = Join-Path $publishRoot $runtime
$sourceDataPath = Join-Path $repoRoot "data"

# Fixed zip name so the GitHub /releases/latest/download/ URL never changes.
$zipPath = Join-Path $repoRoot "QBMBAMM-Windows.zip"

# --- Read current <Version> from the .csproj (no bump; use bump-version.ps1 first if needed) ---
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}
$csproj = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$versionMatch = [regex]::Match($csproj, '<Version>(\d+\.\d+\.\d+)</Version>')
if (-not $versionMatch.Success) {
    throw "Could not find <Version>x.y.z</Version> in $projectPath."
}
$newVersionBare = $versionMatch.Groups[1].Value
$newVersion = "v$newVersionBare"
$publishDir = Join-Path $publishRuntimeRoot $newVersion

Write-Host "Publishing single-file executable..."

# --- Stop any running exe (avoids file locks), then dotnet publish Release single-file ---
$running = Get-Process -Name "QBMBAMM" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running QBModsBrowser.Server process..."
    $running | Stop-Process -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# PlaywrightPlatform must be set explicitly: the Playwright MSBuild target falls back to
# host-OS detection when building cross-platform (e.g. win-x64 on Linux CI), which causes
# it to bundle the wrong node binary. Force win-x64 so node.exe is always included.
dotnet publish $projectPath `
    -c Release `
    -r $runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=false `
    /p:PlaywrightPlatform=win-x64

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $publishDir "QBMBAMM.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish completed but executable not found at: $exePath"
}

# --- Copy app-config.json next to the exe (published builds read BaseDirectory first) ---
$sourceAppConfigPath = Join-Path $repoRoot "app-config.json"
if (Test-Path -LiteralPath $sourceAppConfigPath) {
    Copy-Item -LiteralPath $sourceAppConfigPath -Destination (Join-Path $publishDir "app-config.json") -Force
}

# Removes a path (file or directory) if it exists; used to strip user-specific or cached items from the release bundle.
function Remove-IfExists([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

# --- Optional: bundle repo `data` into the publish folder, minus private/cache artifacts ---
# Seed release with current repo `data` snapshot, then drop machine/user-specific paths from the copy.
if (Test-Path -LiteralPath $sourceDataPath) {
    $publishDataPath = Join-Path $publishDir "data"
    Remove-IfExists $publishDataPath
    Copy-Item -LiteralPath $sourceDataPath -Destination $publishDataPath -Recurse -Force

    Remove-IfExists (Join-Path $publishDataPath "browser-profile")   # Chromium profile; users get their own on first run.
    Remove-IfExists (Join-Path $publishDataPath "external-images")   # Cached remote images; fetched on demand.
    Remove-IfExists (Join-Path $publishDataPath "mod-profiles.json") # User-specific mod profile lists.
    Remove-IfExists (Join-Path $publishDataPath "manager-config.json") # Personal modsPath; regenerated on first run.
}

# --- Zip the publish directory for distribution (one archive per versioned folder) ---
# -Force replaces an existing zip; without it Compress-Archive errors if the file is already there.
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

# --- Create a desktop-style shortcut at the repo root pointing to the published exe ---
# Makes it easy to launch the latest published build directly from the project folder.
$shortcutPath = Join-Path $repoRoot "QBMBAMM.lnk"
$iconPath     = Join-Path $repoRoot "QBSSMB4.ico"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath      = $exePath
$shortcut.WorkingDirectory = $publishDir
if (Test-Path -LiteralPath $iconPath) {
    $shortcut.IconLocation = "$iconPath,0"
}
$shortcut.Description = "QBMBAMM $newVersion"
$shortcut.Save()

# --- Summary ---
Write-Host ""
Write-Host "Done."
Write-Host "Version: $newVersion"
Write-Host "Executable: $exePath"
Write-Host "Shortcut:   $shortcutPath"
Write-Host "Zip: $zipPath"
if (Test-Path -LiteralPath $sourceDataPath) {
    Write-Host "Bundled data: $sourceDataPath (excluding browser-profile, external-images)"
}
