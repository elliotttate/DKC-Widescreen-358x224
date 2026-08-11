param(
    [string]$GameRoot = $env:SUPERZSNES_ROOT,
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration }

$sourceDll = Join-Path $PSScriptRoot "bin\$Configuration\netstandard2.1\DKCLevelAutomation.dll"
if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) { throw "Plugin DLL does not exist: $sourceDll" }
if (-not (Test-Path -LiteralPath $GameRoot -PathType Container)) { throw "Game root does not exist: $GameRoot" }

$destination = Join-Path $GameRoot "BepInEx\plugins\DKCLevelAutomation"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $destination "DKCLevelAutomation.dll") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "cli") -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "examples") -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "recipes") -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") -Destination (Join-Path $destination "README.md") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "run-regression-suite.ps1") -Destination (Join-Path $destination "run-regression-suite.ps1") -Force

Write-Host "Installed into $destination"
Write-Host "The installer did not launch or stop SuperZSNES. Start it yourself when ready."
