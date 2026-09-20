<#
.SYNOPSIS
    Downloads the latest immich-go Windows release and pins it as the bundled version.

    Build revision: 2026-09-20c (Windows x86_64 asset matching fix)

.DESCRIPTION
    Fetches the latest release from GitHub, extracts immich-go.exe into tools\immich-go,
    and records the pinned version in tools\immich-go\pinned-version.txt. The app reads
    this at startup to display the version and locate the binary.

    Re-run any time to update the pinned version ("check for updates").

.PARAMETER Repo
    GitHub repo to download from. Defaults to the actively maintained fork.
#>
param(
    [string]$Repo = "sweepies/immich-go"
)

$ErrorActionPreference = "Stop"

$toolsDir = Join-Path (Join-Path $PSScriptRoot "tools") "immich-go"
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

Write-Host "Fetching latest release for $Repo ..." -ForegroundColor Cyan
$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/$Repo/releases/latest" `
    -Headers @{ "User-Agent" = "ImmichAutoUploader" }

$version = $release.tag_name
Write-Host "Latest version: $version" -ForegroundColor Green

$asset = @($release.assets | Where-Object { $_.name -match '(?i)windows.*(x86_64|amd64|x64).*\.zip$' })[0]
if (-not $asset) {
    Write-Host "Could not find a Windows x64 zip asset. Available assets:" -ForegroundColor Red
    $release.assets | ForEach-Object { Write-Host "  $($_.name)" }
    throw "No matching immich-go asset found."
}

Write-Host "Downloading $($asset.name) ..." -ForegroundColor Cyan
$zipPath = Join-Path $toolsDir "immich-go-download.zip"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

Write-Host "Extracting ..." -ForegroundColor Cyan
Expand-Archive -Path $zipPath -DestinationPath $toolsDir -Force
Remove-Item $zipPath -Force

$exe = Get-ChildItem -Path $toolsDir -Recurse -Filter "immich-go.exe" | Select-Object -First 1
if (-not $exe) {
    throw "Extraction succeeded but immich-go.exe was not found under $toolsDir."
}

# Flatten: ensure the exe sits directly in tools\immich-go for a stable path.
$finalExe = Join-Path $toolsDir "immich-go.exe"
if ($exe.FullName -ne $finalExe) {
    Move-Item -LiteralPath $exe.FullName -Destination $finalExe -Force
}

$version | Out-File -FilePath (Join-Path $toolsDir "pinned-version.txt") -NoNewline

Write-Host ""
Write-Host "Pinned immich-go $version" -ForegroundColor Green
Write-Host "Binary : $finalExe"
Write-Host ""
Write-Host "Next: dotnet build ImmichAutoUploader.sln -c Release"
