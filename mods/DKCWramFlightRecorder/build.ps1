param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$GameManagedDir = $env:SUPERZSNES_MANAGED_DIR,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$downloadsDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory '..\..\..\..'))
$repoDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory '..\..'))

if (-not $BepInExRoot) {
    $candidates = @(
        (Join-Path $repoDirectory '.deps\BepInEx'),
        (Join-Path $downloadsDirectory 'BepInEx_x86_5.4.23.5_extracted')
    )
    $BepInExRoot = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'BepInEx\core\BepInEx.dll') } | Select-Object -First 1
}
if (-not $GameManagedDir) {
    $candidates = @(
        (Join-Path $repoDirectory '.deps\SuperZSNES\SUPERZSNES_Data\Managed'),
        (Join-Path $downloadsDirectory 'SuperZSNES_v0.230\SUPERZSNES_Data\Managed')
    )
    $GameManagedDir = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Assembly-CSharp.dll') } | Select-Object -First 1
}

if (-not $BepInExRoot -or -not (Test-Path -LiteralPath (Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'))) {
    throw 'BepInExRoot is missing BepInEx\core\BepInEx.dll. Pass -BepInExRoot or set BEPINEX_ROOT.'
}
if (-not $GameManagedDir -or -not (Test-Path -LiteralPath (Join-Path $GameManagedDir 'Assembly-CSharp.dll'))) {
    throw 'GameManagedDir is missing Assembly-CSharp.dll. Pass -GameManagedDir or set SUPERZSNES_MANAGED_DIR.'
}

dotnet build (Join-Path $projectDirectory 'DKCWramFlightRecorder.csproj') -c $Configuration `
    "-p:BepInExRoot=$BepInExRoot" "-p:GameManagedDir=$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

dotnet run --project (Join-Path $projectDirectory 'Tests\DKCWramFlightRecorder.Tests.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "offline C# tests failed with exit code $LASTEXITCODE" }

$oldDontWriteBytecode = $env:PYTHONDONTWRITEBYTECODE
try {
    $env:PYTHONDONTWRITEBYTECODE = '1'
    python -B -m unittest discover -s (Join-Path $projectDirectory 'Tests') -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw "offline Python tests failed with exit code $LASTEXITCODE" }
}
finally { $env:PYTHONDONTWRITEBYTECODE = $oldDontWriteBytecode }

& (Join-Path $projectDirectory 'verify.ps1') -BepInExRoot $BepInExRoot -GameManagedDir $GameManagedDir -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "offline verifier failed with exit code $LASTEXITCODE" }

Write-Output "Built and verified only: $(Join-Path $projectDirectory "bin\$Configuration\netstandard2.1\DKCWramFlightRecorder.dll")"
Write-Output 'No plugin was installed and no emulator process or bridge was contacted.'
