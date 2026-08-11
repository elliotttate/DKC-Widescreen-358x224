param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$SuperZSNESRoot = $env:SUPERZSNES_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not $BepInExRoot) { throw 'Pass -BepInExRoot or set BEPINEX_ROOT.' }
if (-not $SuperZSNESRoot) { throw 'Pass -SuperZSNESRoot or set SUPERZSNES_ROOT.' }

$managed = Join-Path $SuperZSNESRoot 'SUPERZSNES_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) {
    throw "SuperZSNES managed directory is invalid: $managed"
}
if (-not (Test-Path (Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInEx root is invalid: $BepInExRoot"
}

$projects = Get-ChildItem -Directory (Join-Path $PSScriptRoot 'mods') |
    ForEach-Object { Get-ChildItem -File $_.FullName -Filter '*.csproj' | Select-Object -First 1 } |
    Where-Object { $_ }

$results = foreach ($project in $projects) {
    Write-Host "Building $($project.BaseName)..." -ForegroundColor Cyan
    & dotnet build $project.FullName -c $Configuration `
        -p:BepInExRoot=$BepInExRoot `
        -p:SuperZSNESRoot=$SuperZSNESRoot `
        -p:GameManagedDir=$managed 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    [pscustomobject]@{ Project = $project.BaseName; ExitCode = $exitCode }
}

$failed = @($results | Where-Object ExitCode -ne 0)
$results | Format-Table -AutoSize
if ($failed.Count) { throw "$($failed.Count) project build(s) failed." }
Write-Host "Built $($results.Count) plugin projects successfully." -ForegroundColor Green
