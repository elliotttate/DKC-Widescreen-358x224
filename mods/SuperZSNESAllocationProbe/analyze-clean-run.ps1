param(
    [string] $RuntimePauseEvents = $env:RUNTIME_PAUSE_EVENTS,
    [string] $AudioWindows = $env:AUDIO_TIMING_WINDOWS,
    [datetime] $StartUtc = [datetime]'2026-08-11T17:40:37Z',
    [datetime] $EndUtc = [datetime]'2026-08-11T17:41:46.4Z'
)

$ErrorActionPreference = 'Stop'
$periodic = @(Get-Content -LiteralPath $RuntimePauseEvents | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.kind -eq 'periodic' -and [datetime]$_.utc -ge $StartUtc -and [datetime]$_.utc -le $EndUtc })
$windows = @(Get-Content -LiteralPath $AudioWindows | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { [datetime]$_.utc -ge $StartUtc.AddSeconds(3) -and [datetime]$_.utc -le $EndUtc })
if ($periodic.Count -lt 2 -or $windows.Count -lt 2) { throw 'The requested interval has insufficient samples.' }

$seconds = ($windows | ForEach-Object { $_.windowMs } | Measure-Object -Sum).Sum / 1000
$updates = ($windows | ForEach-Object { $_.hostUpdate.duration.count } | Measure-Object -Sum).Sum
$frames = ($windows | ForEach-Object { $_.runFrame.count } | Measure-Object -Sum).Sum
$updateWeightedUs = ($windows | ForEach-Object { $_.hostUpdate.duration.avgUs * $_.hostUpdate.duration.count } | Measure-Object -Sum).Sum
$frameWeightedUs = ($windows | ForEach-Object { $_.runFrame.avgUs * $_.runFrame.count } | Measure-Object -Sum).Sum

$aligned = @()
for ($index = 1; $index -lt $periodic.Count; $index++) {
    $sample = $periodic[$index]
    $prior = $periodic[$index - 1]
    $window = $windows | Sort-Object { [math]::Abs((([datetime]$_.utc) - ([datetime]$sample.utc)).TotalMilliseconds) } |
        Select-Object -First 1
    $aligned += [pscustomobject]@{
        gcDelta = [double]($sample.gc.gen0 - $prior.gc.gen0)
        updateHz = [double]($window.hostUpdate.duration.count / ($window.windowMs / 1000))
    }
}
$meanGc = ($aligned.gcDelta | Measure-Object -Average).Average
$meanHz = ($aligned.updateHz | Measure-Object -Average).Average
$numerator = 0.0
$gcSquares = 0.0
$hzSquares = 0.0
foreach ($row in $aligned) {
    $gc = $row.gcDelta - $meanGc
    $hz = $row.updateHz - $meanHz
    $numerator += $gc * $hz
    $gcSquares += $gc * $gc
    $hzSquares += $hz * $hz
}
$correlation = if ($gcSquares -eq 0 -or $hzSquares -eq 0) { [double]::NaN } else { $numerator / [math]::Sqrt($gcSquares * $hzSquares) }
$firstPeriodic = $periodic[0]
$lastPeriodic = $periodic[$periodic.Count - 1]
$gcSeconds = (([datetime]$lastPeriodic.utc) - ([datetime]$firstPeriodic.utc)).TotalSeconds

[pscustomobject]@{
    startUtc = $StartUtc.ToUniversalTime().ToString('o')
    endUtc = $EndUtc.ToUniversalTime().ToString('o')
    audioWindows = $windows.Count
    measuredSeconds = [math]::Round($seconds, 6)
    emulatedFrames = $frames
    emulatedFrameHz = [math]::Round($frames / $seconds, 6)
    hostUpdates = $updates
    hostUpdateHz = [math]::Round($updates / $seconds, 6)
    hostUpdateAverageMs = [math]::Round($updateWeightedUs / $updates / 1000, 6)
    runFrameAverageMs = [math]::Round($frameWeightedUs / $frames / 1000, 6)
    maximumHostCadenceMs = [math]::Round(($windows | ForEach-Object { $_.hostUpdate.cadence.maxUs } | Measure-Object -Maximum).Maximum / 1000, 6)
    maximumRunFrameStartGapMs = [math]::Round(($windows | ForEach-Object { $_.runFrameCadence.maxUs } | Measure-Object -Maximum).Maximum / 1000, 6)
    collectionsObserved = $lastPeriodic.gc.gen0 - $firstPeriodic.gc.gen0
    collectionsPerSecond = [math]::Round(($lastPeriodic.gc.gen0 - $firstPeriodic.gc.gen0) / $gcSeconds, 6)
    managedBytesMinimum = ($periodic.managedBytes | Measure-Object -Minimum).Minimum
    managedBytesMaximum = ($periodic.managedBytes | Measure-Object -Maximum).Maximum
    gcDeltaToUpdateHzPearson = [math]::Round($correlation, 6)
} | ConvertTo-Json
