param(
    [string]$GameRoot = $env:SUPERZSNES_ROOT
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $projectRoot 'bin\Release\netstandard2.1\SuperZSNESPerformanceGuard.dll'
$pluginDirectory = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESPerformanceGuard'

dotnet build (Join-Path $projectRoot 'SuperZSNESPerformanceGuard.csproj') -c Release
New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginDirectory 'SuperZSNESPerformanceGuard.dll') -Force
Write-Host "Installed SuperZSNESPerformanceGuard to $pluginDirectory"
