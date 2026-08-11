param(
    [string]$GameDir = $env:SUPERZSNES_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameExe = Join-Path $GameDir 'SUPERZSNES.exe'
$bepInExDll = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$sourceDll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\DKCWidescreenDebugger.dll"
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\DKCWidescreenDebugger'

if (-not (Test-Path -LiteralPath $gameExe)) { throw "SUPERZSNES.exe was not found in $GameDir" }
if (-not (Test-Path -LiteralPath $bepInExDll)) { throw "Install the BepInEx 5 x86 package into the game directory first." }
if (-not (Test-Path -LiteralPath $sourceDll)) { throw "Build the project first; DLL not found at $sourceDll" }

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $pluginDir 'DKCWidescreenDebugger.dll') -Force
Write-Host "Installed plugin to $pluginDir"
