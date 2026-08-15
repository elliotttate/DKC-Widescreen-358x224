param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SkipBuild) {
    & (Join-Path $projectDir 'build.ps1') -BepInExRoot $GameDir -GameManagedDir (Join-Path $GameDir 'SUPERZSNES_Data\Managed')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$destination = Join-Path $GameDir 'BepInEx\plugins\DKCPlaytestRecorder'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDir 'bin\Release\net472\DKCPlaytestRecorder.dll') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'request-report.ps1') -Destination $destination -Force
New-Item -ItemType Directory -Path (Join-Path $destination 'cli') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDir 'cli\replay_bundle.py') -Destination (Join-Path $destination 'cli') -Force
Write-Host "Installed DKCPlaytestRecorder to $destination. Restart SuperZSNES to load it."
