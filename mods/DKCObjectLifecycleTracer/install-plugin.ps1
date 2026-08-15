param(
    [string]$GameDir = $env:SUPERZSNES_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameExe = Join-Path $GameDir 'SUPERZSNES.exe'
$bepInExDll = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
$sourceDll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\DKCObjectLifecycleTracer.dll"
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\DKCObjectLifecycleTracer'

if (-not (Test-Path -LiteralPath $gameExe)) { throw "SUPERZSNES.exe was not found in $GameDir" }
if (-not (Test-Path -LiteralPath $bepInExDll)) { throw 'Install BepInEx 5 x86 into SuperZSNES first.' }
if (-not (Test-Path -LiteralPath $sourceDll)) { throw "Build the tracer first; DLL not found at $sourceDll" }

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $pluginDir 'DKCObjectLifecycleTracer.dll') -Force
Write-Host "Installed the diagnostic tracer to $pluginDir. The script did not start or stop SuperZSNES."
