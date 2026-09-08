<#
.SYNOPSIS
  Packs mod/Data into itemswap.pak (stored, uncompressed - matches CryEngine's
  pak reader expectations) and installs it into the RETAIL KCD2 Mods folder.

.DESCRIPTION
  Deploy target (per the plan, no Mods folder exists there yet - this script
  creates it):
    <RetailInstall>\Mods\itemswap\mod.manifest
    <RetailInstall>\Mods\itemswap\Data\itemswap.pak

  The pak's internal root is the CONTENTS of mod\Data (e.g. Scripts/Startup/
  itemswap.lua), not the Data folder itself, so it overlays correctly.

.PARAMETER RetailInstall
  Path to your KCD2 install. If omitted, you'll be prompted for it
  interactively (default shown matches the standard Steam install
  location - override it if yours is a custom Steam library folder). Pass
  it explicitly to skip the prompt, e.g. for a scripted/CI run.
#>

param(
    [string]$RetailInstall
)

$ErrorActionPreference = "Stop"

# The hardcoded default only matches the standard Steam install location -
# anyone with a custom Steam library folder (a second drive, a custom path
# picked at install time) needs a different one. Prompt for it instead of
# silently assuming it, unless the caller already passed -RetailInstall
# explicitly or stdin isn't interactive (a scripted/CI run).
$defaultRetailInstall = "C:\Program Files (x86)\Steam\steamapps\common\KingdomComeDeliverance2"
if (-not $PSBoundParameters.ContainsKey('RetailInstall')) {
    if ([Console]::IsInputRedirected) {
        $RetailInstall = $defaultRetailInstall
    } else {
        $answer = Read-Host "Path to your KCD2 install (adjust if your Steam library isn't in the default location) [$defaultRetailInstall]"
        $RetailInstall = if ([string]::IsNullOrWhiteSpace($answer)) { $defaultRetailInstall } else { $answer }
    }
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$dataSrc    = Join-Path $repoRoot "mod\Data"
$manifest   = Join-Path $repoRoot "mod\mod.manifest"
$installDir = Join-Path $RetailInstall "Mods\itemswap"
$dataDstDir = Join-Path $installDir "Data"
$pakPath    = Join-Path $dataDstDir "itemswap.pak"

if (-not (Test-Path $dataSrc))  { throw "Mod source not found: $dataSrc" }
if (-not (Test-Path $manifest)) { throw "mod.manifest not found: $manifest" }
if (-not (Test-Path $RetailInstall)) { throw "Retail install not found: $RetailInstall" }

New-Item -ItemType Directory -Force -Path $dataDstDir | Out-Null

if (Test-Path $pakPath) { Remove-Item $pakPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($pakPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -Path $dataSrc -Recurse -File | ForEach-Object {
        $relPath = $_.FullName.Substring($dataSrc.Length + 1).Replace("\", "/")
        Write-Host "  + $relPath"
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, $relPath,
            [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
} finally {
    $zip.Dispose()
}

Copy-Item $manifest -Destination $installDir -Force

Write-Host ""
Write-Host "Installed:"
Write-Host "  $installDir\mod.manifest"
Write-Host "  $pakPath"
Write-Host ""
Write-Host "Launch retail with -devmode and load a save to test:"
Write-Host "  cmd /c start `"`" `"$RetailInstall\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe`" -devmode"
