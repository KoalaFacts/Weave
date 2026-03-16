#!/usr/bin/env pwsh
# Weave CLI installer for Windows
# Usage:  irm https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.ps1 | iex
# Pinned: $env:WEAVE_VERSION='0.1.0'; irm ... | iex

$ErrorActionPreference = 'Stop'

$repo = "KoalaFacts/Weave"
$toolName = "weave"
$installDir = "$env:LOCALAPPDATA\weave"
$binPath = "$installDir\weave.exe"

function Write-Status($msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Success($msg) { Write-Host "  $msg" -ForegroundColor Green }
function Write-Err($msg) { Write-Host "  $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "  Weave CLI Installer" -ForegroundColor Magenta
Write-Host ""

# Detect architecture
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
} else {
    Write-Err "32-bit Windows is not supported."
    exit 1
}

$rid = "win-$arch"
Write-Status "Detected platform: $rid"

# Resolve version
if ($env:WEAVE_VERSION) {
    $tag = "v$env:WEAVE_VERSION"
    Write-Status "Using requested version: $tag"
    $version = $env:WEAVE_VERSION
    $assetName = "weave-$version-$rid.zip"
    $downloadUrl = "https://github.com/$repo/releases/download/$tag/$assetName"
} else {
    Write-Status "Finding latest release..."
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ Accept = "application/vnd.github+json" }
    $tag = $release.tag_name
    Write-Status "Latest version: $tag"
    $version = $tag.TrimStart('v')
    $assetName = "weave-$version-$rid.zip"

    $asset = $release.assets | Where-Object { $_.name -eq $assetName }
    if (-not $asset) {
        Write-Err "Could not find asset '$assetName' in release $tag."
        Write-Err "Available assets: $($release.assets.name -join ', ')"
        exit 1
    }
    $downloadUrl = $asset.browser_download_url
}

# Download
$tmpFile = Join-Path $env:TEMP "weave-install.zip"
Write-Status "Downloading $assetName..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpFile -UseBasicParsing

# Install
Write-Status "Installing to $installDir..."
if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Expand-Archive -Path $tmpFile -DestinationPath $installDir -Force
Remove-Item $tmpFile -Force

if (-not (Test-Path $binPath)) {
    Write-Err "Installation failed — $binPath not found after extraction."
    exit 1
}

# Add to PATH if needed
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    Write-Status "Adding $installDir to your PATH..."
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$installDir", "User")
    $env:Path = "$env:Path;$installDir"
}

Write-Host ""
Write-Success "Weave CLI $tag installed successfully!"
Write-Host ""
Write-Host "  Get started:" -ForegroundColor White
Write-Host "    weave workspace new demo"
Write-Host "    weave workspace up demo"
Write-Host ""
