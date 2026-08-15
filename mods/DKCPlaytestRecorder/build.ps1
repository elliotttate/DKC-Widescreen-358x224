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

dotnet build (Join-Path $projectDir 'DKCPlaytestRecorder.csproj') -c $Configuration `
    "-p:BepInExRoot=$BepInExRoot" "-p:GameManagedDir=$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE" }

dotnet run --project (Join-Path $projectDir 'Tests\DKCPlaytestRecorder.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Model tests failed with exit code $LASTEXITCODE" }

python -m py_compile (Join-Path $projectDir 'cli\replay_bundle.py')
if ($LASTEXITCODE -ne 0) { throw "Python compile failed with exit code $LASTEXITCODE" }

Write-Host 'Built and verified DKCPlaytestRecorder.dll'
