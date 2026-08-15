param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$GameManagedDir = $env:SUPERZSNES_MANAGED_DIR,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path (Split-Path $projectDir -Parent) -Parent
if (-not $BepInExRoot) { $BepInExRoot = Join-Path $repoRoot '.deps\BepInEx' }
if (-not $GameManagedDir) { $GameManagedDir = Join-Path $repoRoot '.deps\SuperZSNES\SUPERZSNES_Data\Managed' }

if (-not (Test-Path -LiteralPath (Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInExRoot does not contain BepInEx\core\BepInEx.dll: $BepInExRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameManagedDir 'UnityEngine.CoreModule.dll'))) {
    throw "GameManagedDir does not contain UnityEngine.CoreModule.dll: $GameManagedDir"
}

dotnet build (Join-Path $projectDir 'DKCDebugInvincibility.csproj') -c $Configuration `
    "-p:BepInExRoot=$BepInExRoot" "-p:GameManagedDir=$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

dotnet run --project (Join-Path $projectDir 'Tests\DKCDebugInvincibility.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "offline tests failed with exit code $LASTEXITCODE" }

Write-Host 'Built and verified DKCDebugInvincibility.dll'
