param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$GameManagedDir = $env:SUPERZSNES_MANAGED_DIR,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDir 'DKCTilemapInspector.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInExRoot does not contain BepInEx\core\BepInEx.dll: $BepInExRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameManagedDir 'UnityEngine.CoreModule.dll'))) {
    throw "GameManagedDir does not contain UnityEngine.CoreModule.dll: $GameManagedDir"
}

dotnet build $projectFile -c $Configuration "-p:BepInExRoot=$BepInExRoot" "-p:GameManagedDir=$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\DKCTilemapInspector.dll"
Write-Host "Built $dll"
