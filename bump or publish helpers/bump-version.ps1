param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("minor", "patch")]
    [string]$BumpType,

    [Parameter(Mandatory = $true)]
    [string]$ProjectPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Reads the current <Version> from the given .csproj, increments it per BumpType, writes it back, and outputs the new bare version string.
if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Project file not found: $ProjectPath"
}

$csproj = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
$versionMatch = [regex]::Match($csproj, '<Version>(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)</Version>')
if (-not $versionMatch.Success) {
    throw "Could not find <Version>x.y.z</Version> in $ProjectPath."
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
$newVersionBare = "{0}.{1}.{2}" -f $major, $minor, $patch
$newVersion = "v$newVersionBare"

$updatedCsproj = [regex]::Replace($csproj, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newVersionBare</Version>", 1)
Set-Content -LiteralPath $ProjectPath -Value $updatedCsproj -Encoding UTF8

Write-Host "Version bumped: $oldVersion -> $newVersion"

# Output bare version for callers to capture.
Write-Output $newVersionBare
