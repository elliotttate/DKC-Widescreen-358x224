param(
    [string]$GameDir = $env:SUPERZSNES_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameExe = Join-Path $GameDir 'SUPERZSNES.exe'
$bepInExDll = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$sourceDll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\DKCSoftlockWatchdog.dll"
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\DKCSoftlockWatchdog'

if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) { throw "SUPERZSNES.exe was not found in $GameDir" }
if (-not (Test-Path -LiteralPath $bepInExDll -PathType Leaf)) { throw 'Install BepInEx 5 x86 into SuperZSNES first.' }
if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) { throw "Build the watchdog first; DLL not found at $sourceDll" }

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $pluginDir 'DKCSoftlockWatchdog.dll') -Force
Write-Host "Installed DKCSoftlockWatchdog.dll to $pluginDir. The script did not launch, stop, restart, or contact SuperZSNES."
