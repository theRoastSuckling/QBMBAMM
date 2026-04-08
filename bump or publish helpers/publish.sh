#!/usr/bin/env bash
set -euo pipefail

# Publishes a self-contained win-x64 single-file exe, bundles data/, and zips the result.
# Works on Linux (zip) and Windows/Git Bash (falls back to PowerShell Compress-Archive).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

PROJECT="$REPO_ROOT/src/QBModsBrowser.Server/QBModsBrowser.Server.csproj"
RUNTIME="win-x64"

# Read version from the .csproj; run bump or publish helpers/bump-version.ps1 first if a bump is needed.
VERSION=$(grep -oP '(?<=<Version>)\d+\.\d+\.\d+(?=</Version>)' "$PROJECT")
[ -n "$VERSION" ] || { echo "Could not read <Version> from $PROJECT" >&2; exit 1; }
TAG="v$VERSION"

PUBLISH_DIR="$REPO_ROOT/publish/$RUNTIME/$TAG"
# Fixed name so the GitHub /releases/latest/download/ URL never changes.
ZIP="$REPO_ROOT/QBMBAMM-Windows.zip"

echo "Publishing $TAG..."
mkdir -p "$PUBLISH_DIR"

# Use -p: (not /p:) so bash does not treat MSBuild switches as paths.
dotnet publish "$PROJECT" \
  -c Release -r "$RUNTIME" --self-contained true \
  -o "$PUBLISH_DIR" \
  -p:EnableWindowsTargeting=true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false

# Copy app-config.json next to the exe so published builds resolve it from BaseDirectory.
[ -f "$REPO_ROOT/app-config.json" ] && cp "$REPO_ROOT/app-config.json" "$PUBLISH_DIR/"

# Bundle repo data/ snapshot; strip machine/user-specific paths before packaging.
if [ -d "$REPO_ROOT/data" ]; then
  rm -rf "$PUBLISH_DIR/data"
  cp -r "$REPO_ROOT/data" "$PUBLISH_DIR/data"
  rm -rf \
    "$PUBLISH_DIR/data/browser-profile" \
    "$PUBLISH_DIR/data/external-images" \
    "$PUBLISH_DIR/data/mod-profiles.json" \
    "$PUBLISH_DIR/data/manager-config.json"
fi

rm -f "$ZIP"
# Use zip when available (Linux/CI); fall back to PowerShell Compress-Archive on Windows/Git Bash.
if command -v zip &>/dev/null; then
  (cd "$PUBLISH_DIR" && zip -qr "$ZIP" .)
else
  WIN_SRC=$(cygpath -w "$PUBLISH_DIR")
  WIN_ZIP=$(cygpath -w "$ZIP")
  powershell.exe -NoProfile -Command \
    "Compress-Archive -Path '$WIN_SRC\\*' -DestinationPath '$WIN_ZIP' -Force"
fi

echo ""
echo "Done. Version: $TAG"
echo "Zip: $ZIP"
