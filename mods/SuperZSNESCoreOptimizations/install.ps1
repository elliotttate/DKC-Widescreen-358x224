param([string]$GameRoot = $env:SUPERZSNES_ROOT)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build (Join-Path $root 'SuperZSNESCoreOptimizations.csproj') -c Release
$target = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESCoreOptimizations'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'bin\Release\net472\SuperZSNESCoreOptimizations.dll') -Destination (Join-Path $target 'SuperZSNESCoreOptimizations.dll') -Force
Write-Host "Installed SuperZSNESCoreOptimizations to $target"
