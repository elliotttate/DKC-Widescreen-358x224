param(
    [string]$Configuration = "Release",
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$GameManagedDir = $env:SUPERZSNES_MANAGED_DIR
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "DKCLevelAutomation.csproj"
dotnet build $project -c $Configuration -p:BepInExRoot=$BepInExRoot -p:GameManagedDir=$GameManagedDir
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $PSScriptRoot "bin\$Configuration\netstandard2.1\DKCLevelAutomation.dll"
Write-Host "Built $dll"
