param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$GameManagedDir = $env:SUPERZSNES_MANAGED_DIR,
    [string]$CleanRomPath = $env:DKC_CLEAN_ROM,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $BepInExRoot) { $BepInExRoot = Join-Path (Split-Path (Split-Path $projectDir -Parent) -Parent) '.deps\BepInEx' }
if (-not $GameManagedDir) { $GameManagedDir = Join-Path (Split-Path (Split-Path $projectDir -Parent) -Parent) '.deps\SuperZSNES\SUPERZSNES_Data\Managed' }

if (-not (Test-Path -LiteralPath (Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInExRoot does not contain BepInEx\core\BepInEx.dll: $BepInExRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameManagedDir 'UnityEngine.CoreModule.dll'))) {
    throw "GameManagedDir does not contain UnityEngine.CoreModule.dll: $GameManagedDir"
}

dotnet build (Join-Path $projectDir 'DKCObjectLifecycleTracer.csproj') -c $Configuration `
    "-p:BepInExRoot=$BepInExRoot" "-p:GameManagedDir=$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$oldCleanRom = $env:DKC_CLEAN_ROM
try {
    if ($CleanRomPath) {
        if (-not (Test-Path -LiteralPath $CleanRomPath -PathType Leaf)) { throw "CleanRomPath does not exist: $CleanRomPath" }
        $env:DKC_CLEAN_ROM = (Resolve-Path -LiteralPath $CleanRomPath).Path
    }
    dotnet run --project (Join-Path $projectDir 'Tests\DKCObjectLifecycleTracer.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "offline tests failed with exit code $LASTEXITCODE" }
}
finally {
    $env:DKC_CLEAN_ROM = $oldCleanRom
}

Write-Host "Built and verified DKCObjectLifecycleTracer.dll"
