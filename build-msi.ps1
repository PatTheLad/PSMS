<#
.SYNOPSIS
  Builds the Windows x64 PSMS Setup EXE (checks/installs .NET 10, then MSI).

.DESCRIPTION
  1. Downloads the ASP.NET Core 10 x64 runtime installer into redist\ (if missing)
  2. Publishes PSMS.App as win-x64 framework-dependent
  3. Builds the WiX MSI
  4. Builds the WiX Burn bootstrapper EXE that:
       - detects ASP.NET Core 10
       - installs it when missing
       - installs PSMS

.NOTES
  Run on Windows 10/11 with the .NET 10 SDK.
  Target PCs also need WebView2 (usually already installed with Edge).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Version = '1.0.0',

    [string] $OutputDir = '',

    # Official aka.ms link always resolves to the latest 10.0.x ASP.NET Core x64 runtime
    [string] $AspNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x64.exe'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path $PSScriptRoot
$installerProj = Join-Path $repoRoot 'src/PSMS.Installer/PSMS.Installer.wixproj'
$bundleProj = Join-Path $repoRoot 'src/PSMS.Bundle/PSMS.Bundle.wixproj'
$appProj = Join-Path $repoRoot 'src/PSMS.App/PSMS.App.csproj'
$redistDir = Join-Path $repoRoot 'redist'
$runtimeExe = Join-Path $redistDir 'aspnetcore-runtime-win-x64.exe'

if (-not (Test-Path $installerProj)) {
    throw "Installer project not found at $installerProj (run this script from the repo root)."
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'artifacts'
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $redistDir | Out-Null

if (-not (Test-Path $runtimeExe)) {
    Write-Host "Downloading ASP.NET Core 10 Runtime (x64)..." -ForegroundColor Cyan
    Write-Host "  $AspNetRuntimeUrl"
    Invoke-WebRequest -Uri $AspNetRuntimeUrl -OutFile $runtimeExe -UseBasicParsing
    if (-not (Test-Path $runtimeExe) -or ((Get-Item $runtimeExe).Length -lt 1MB)) {
        throw "Failed to download ASP.NET Core 10 runtime installer."
    }
    Write-Host ("  Saved {0:N1} MB → {1}" -f ((Get-Item $runtimeExe).Length / 1MB), $runtimeExe)
} else {
    Write-Host "Using cached runtime redistributable:" -ForegroundColor DarkGray
    Write-Host "  $runtimeExe"
}

Write-Host "Publishing PSMS.App (win-x64, framework-dependent, $Configuration)..." -ForegroundColor Cyan
$publishDir = Join-Path $OutputDir 'publish/win-x64'
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish $appProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Ensure window icon sits next to the EXE for Photino
$iconSrc = Join-Path $repoRoot 'src/PSMS.App/wwwroot/appicon.png'
$icoSrc = Join-Path $repoRoot 'src/PSMS.App/wwwroot/favicon.ico'
if (Test-Path $iconSrc) { Copy-Item $iconSrc (Join-Path $publishDir 'appicon.png') -Force }
if (Test-Path $icoSrc) { Copy-Item $icoSrc (Join-Path $publishDir 'favicon.ico') -Force }

Write-Host "Building MSI + Setup bootstrapper (WiX, $Configuration, version $Version)..." -ForegroundColor Cyan
dotnet build $bundleProj `
    -c $Configuration `
    -p:Version=$Version `
    -p:ProductVersion=$Version

if ($LASTEXITCODE -ne 0) {
    throw "WiX bundle build failed with exit code $LASTEXITCODE"
}

$msiBuilt = Get-ChildItem -Path (Join-Path $repoRoot 'src/PSMS.Installer/bin') -Filter 'PSMS-Setup.msi' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$exeBuilt = Get-ChildItem -Path (Join-Path $repoRoot 'src/PSMS.Bundle/bin') -Filter 'PSMS-Setup.exe' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $exeBuilt) {
    throw "Setup EXE was not produced. Check the WiX bundle build output."
}

$exeOut = Join-Path $OutputDir "PSMS-Setup-$Version-win-x64.exe"
Copy-Item $exeBuilt.FullName $exeOut -Force

if ($msiBuilt) {
    $msiOut = Join-Path $OutputDir "PSMS-Setup-$Version-win-x64.msi"
    Copy-Item $msiBuilt.FullName $msiOut -Force
}

Write-Host ""
Write-Host "Setup ready (recommended):" -ForegroundColor Green
Write-Host "  $exeOut"
Write-Host ""
Write-Host "This EXE will:" -ForegroundColor Yellow
Write-Host "  1. Detect ASP.NET Core 10 (x64)"
Write-Host "  2. Install it automatically if missing"
Write-Host "  3. Install PSMS"
if ($msiBuilt) {
    Write-Host ""
    Write-Host "Standalone MSI (requires .NET 10 already installed):"
    Write-Host "  $msiOut"
}
