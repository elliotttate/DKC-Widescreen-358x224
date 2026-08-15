param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Note = 'playtester report'
)

$ErrorActionPreference = 'Stop'
$pluginDir = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'BepInEx\plugins\DKCPlaytestRecorder'
if (-not (Test-Path -LiteralPath $pluginDir)) {
    throw "DKCPlaytestRecorder is not installed at $pluginDir"
}
$request = Join-Path $pluginDir 'report.request'
[System.IO.File]::WriteAllText($request, $Note, [System.Text.UTF8Encoding]::new($false))
Write-Host "Requested a rolling repro bundle: $request"
