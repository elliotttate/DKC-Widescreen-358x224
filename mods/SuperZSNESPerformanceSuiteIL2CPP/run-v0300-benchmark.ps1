param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory=$true)][string]$RomPath,
    [Parameter(Mandatory=$true)]
    [ValidateSet('stock','stock-atlas','stock-native-atlas','framebuffer-cache-off','framebuffer-cache-on','framebuffer-cache-on-suite','framebuffer-cache-on-stall-stock','framebuffer-cache-on-stall-fixed')]
    [string]$Scenario,
    [Parameter(Mandatory=$true)][int]$Trial,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Runs'),
    [int]$WarmupSeconds = 12,
    [int]$MeasureSeconds = 20,
    [switch]$AllowConfigurationOverwrite
)

$ErrorActionPreference = 'Stop'
if (-not $AllowConfigurationOverwrite) {
    throw 'This benchmark overwrites plugin configs in a disposable game copy. Pass -AllowConfigurationOverwrite.'
}
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$RomPath = (Resolve-Path -LiteralPath $RomPath).Path
$expectedExe = 'B83358E453C9378A37AA0E43D22886AD49EE426F1ECF381B4F84A3A49F54FDD6'
$expectedGameAssembly = '0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A'
$validRoms = @(
    'B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D',
    'FD2950B3AAE287E24F8D8B665AFBC3BE0EC3EEC07AA19DE055427DF76BD46AF5'
)
$exe = Join-Path $GameRoot 'SUPERZSNES.exe'
$gameAssembly = Join-Path $GameRoot 'GameAssembly.dll'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash -ne $expectedExe -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash -ne $expectedGameAssembly) {
    throw 'GameRoot is not the verified SuperZSNES v0.300 x86 IL2CPP build.'
}
$romHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $RomPath).Hash
if ($romHash -notin $validRoms) { throw "ROM is not a verified widescreen build: $romHash" }
$existing = @(Get-Process SUPERZSNES -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($existing.Count) { throw "Close the disposable SuperZSNES process first: $($existing.Id -join ', ')" }

$perfDll = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESPerformanceSuiteIL2CPP\SuperZSNESPerformanceSuiteIL2CPP.dll'
$rendererDll = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESDKCFramebufferRendererIL2CPP\SuperZSNESDKCFramebufferRendererIL2CPP.dll'
$nativeAtlasDll = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESNativeAtlasDirtyFixIL2CPP\SuperZSNESNativeAtlasDirtyFixIL2CPP.dll'
if (-not (Test-Path -LiteralPath $perfDll) -or -not (Test-Path -LiteralPath $rendererDll) -or
    -not (Test-Path -LiteralPath $nativeAtlasDll)) {
    throw 'Install the performance, framebuffer, and native-atlas IL2CPP plugins into the disposable copy before benchmarking.'
}

$rendererEnabled = $Scenario.StartsWith('framebuffer-')
$retained = $Scenario -in @('framebuffer-cache-on','framebuffer-cache-on-suite','framebuffer-cache-on-stall-stock','framebuffer-cache-on-stall-fixed')
$suite = $Scenario -in @('framebuffer-cache-on-suite','framebuffer-cache-on-stall-fixed')
$atlas = $Scenario -eq 'stock-atlas'
$nativeAtlas = $Scenario -eq 'stock-native-atlas'
$stall = $Scenario -in @('framebuffer-cache-on-stall-stock','framebuffer-cache-on-stall-fixed')
$configRoot = Join-Path $GameRoot 'BepInEx\config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
$perfConfig = Join-Path $configRoot 'dev.local.superzsnes.performance.il2cpp.cfg'
$rendererConfig = Join-Path $configRoot 'dev.local.superzsnes.dkcframebuffer.il2cpp.cfg'
$nativeAtlasConfig = Join-Path $configRoot 'dev.local.superzsnes.nativeatlasdirtyfix.il2cpp.cfg'
@"
[Diagnostics]

Enabled = true
StatusEveryUpdates = 120
InjectStallAfterUpdates = 0
InjectStallMilliseconds = $(if ($stall) { 500 } else { 0 })

[Optimizations]

RecoverDroppedBacklog = $($suite.ToString().ToLowerInvariant())
EmergencyMaxBacklogFrames = 120
DisableHistoryCapture = $($suite.ToString().ToLowerInvariant())
DisableRewindCapture = $($suite.ToString().ToLowerInvariant())
GateAtlasUploadsOnTileDirty = $($atlas.ToString().ToLowerInvariant())
"@ | Set-Content -LiteralPath $perfConfig -Encoding UTF8
@"
[Renderer]

Enabled = $($rendererEnabled.ToString().ToLowerInvariant())
PresentFramebuffer = $($rendererEnabled.ToString().ToLowerInvariant())
ShadowRenderInterval = 0
RetainedBackgrounds = $($retained.ToString().ToLowerInvariant())

[Geometry]

Width = 358
Height = 224
LeftExtension = 51
"@ | Set-Content -LiteralPath $rendererConfig -Encoding UTF8
@"
[Patch]

Enabled = $($nativeAtlas.ToString().ToLowerInvariant())
"@ | Set-Content -LiteralPath $nativeAtlasConfig -Encoding UTF8

$perfStatus = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESPerformanceSuiteIL2CPP\status.json'
$rendererStatus = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESDKCFramebufferRendererIL2CPP\status.json'
$nativeAtlasStatus = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESNativeAtlasDirtyFixIL2CPP\status.json'
$process = $null
try {
    $launchedAt = Get-Date
    $process = Start-Process -FilePath $exe -ArgumentList ('"' + $RomPath + '"') `
        -WorkingDirectory $GameRoot -WindowStyle Minimized -PassThru
    Start-Sleep -Seconds $WarmupSeconds
    $process.Refresh()
    if ($process.HasExited) { throw "SuperZSNES exited during warmup with code $($process.ExitCode)." }
    $statusFile = Get-Item -LiteralPath $perfStatus
    if ($statusFile.LastWriteTime -lt $launchedAt) { throw 'Performance status was not refreshed by this launch.' }
    $startPerf = Get-Content -Raw -LiteralPath $perfStatus | ConvertFrom-Json
    if ($startPerf.state -ne 'active' -or [long]$startPerf.errors -ne 0) {
        throw "Performance plugin is not healthy at measurement start: state=$($startPerf.state), errors=$($startPerf.errors)"
    }
    $nativeStatus = if (Test-Path -LiteralPath $nativeAtlasStatus) {
        Get-Content -Raw -LiteralPath $nativeAtlasStatus | ConvertFrom-Json
    } else { $null }
    if ($nativeAtlas -and ($null -eq $nativeStatus -or $nativeStatus.state -ne 'active' -or
        -not [bool]$nativeStatus.applied -or [int]$nativeStatus.patchedSites -ne 6)) {
        throw "Native atlas patch is not healthy: $($nativeStatus | ConvertTo-Json -Compress)"
    }
    $startRenderer = if ($rendererEnabled -and (Test-Path -LiteralPath $rendererStatus)) {
        Get-Content -Raw -LiteralPath $rendererStatus | ConvertFrom-Json
    } else { $null }
    if ($stall) {
        $stallRequest = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESPerformanceSuiteIL2CPP\stall.request'
        New-Item -ItemType File -Path $stallRequest -Force | Out-Null
    }
    Start-Sleep -Seconds $MeasureSeconds
    $process.Refresh()
    if ($process.HasExited) { throw "SuperZSNES exited during measurement with code $($process.ExitCode)." }
    Start-Sleep -Milliseconds 1100
    $endPerf = Get-Content -Raw -LiteralPath $perfStatus | ConvertFrom-Json
    if ($endPerf.state -ne 'active' -or [long]$endPerf.errors -ne 0) {
        throw "Performance plugin is not healthy at measurement end: state=$($endPerf.state), errors=$($endPerf.errors)"
    }
    $endRenderer = if ($rendererEnabled -and (Test-Path -LiteralPath $rendererStatus)) {
        Get-Content -Raw -LiteralPath $rendererStatus | ConvertFrom-Json
    } else { $null }

    function DeltaAverage($start, $end, [string]$countName, [string]$averageName) {
        $count = [double]$end.$countName - [double]$start.$countName
        if ($count -le 0) { return 0.0 }
        return (([double]$end.$averageName * [double]$end.$countName) -
                ([double]$start.$averageName * [double]$start.$countName)) / $count
    }
    $wall = ([double]$endPerf.sampleStopwatchTicks - [double]$startPerf.sampleStopwatchTicks) /
        [double]$endPerf.stopwatchFrequency
    $cpuSeconds = [double]$endPerf.processCpuSeconds - [double]$startPerf.processCpuSeconds
    $deltaUpdates = [long]$endPerf.updates - [long]$startPerf.updates
    $deltaFrames = [long]$endPerf.runFrames - [long]$startPerf.runFrames
    $deltaPresentations = [long]$endPerf.presentations - [long]$startPerf.presentations
    $histogram = @()
    for ($i = 0; $i -lt 7; $i++) {
        $histogram += [long]$endPerf.runFramesPerUpdate[$i] - [long]$startPerf.runFramesPerUpdate[$i]
    }
    $result = [ordered]@{
        scenario = $Scenario
        trial = $Trial
        utc = (Get-Date).ToUniversalTime().ToString('o')
        romSha256 = $romHash
        wallSeconds = $wall
        cpuSeconds = $cpuSeconds
        cpuCores = $cpuSeconds / $wall
        updateHz = $deltaUpdates / $wall
        runFrameHz = $deltaFrames / $wall
        presentationHz = $deltaPresentations / $wall
        updates = $deltaUpdates
        runFrames = $deltaFrames
        presentations = $deltaPresentations
        averageUpdateMs = DeltaAverage $startPerf $endPerf 'updates' 'averageUpdateMs'
        averageRunFrameMs = DeltaAverage $startPerf $endPerf 'runFrames' 'averageRunFrameMs'
        averagePresentationMs = DeltaAverage $startPerf $endPerf 'presentations' 'averagePresentationMs'
        maxUpdateMs = [double]$endPerf.maxUpdateMs
        maxRunFrameMs = [double]$endPerf.maxRunFrameMs
        maxPresentationMs = [double]$endPerf.maxPresentationMs
        maxUpdateGapMs = [double]$endPerf.maxUpdateGapMs
        runFramesPerUpdate = $histogram
        twoPlusRunFrameShare = if ($deltaUpdates) { (($histogram[2..6] | Measure-Object -Sum).Sum / $deltaUpdates) } else { 0 }
        backlogRecoveries = [long]$endPerf.backlogRecoveries - [long]$startPerf.backlogRecoveries
        retainedBacklogFrameCharges = [long]$endPerf.retainedBacklogFrameCharges -
            [long]$startPerf.retainedBacklogFrameCharges
        guardedUpdates = [long]$endPerf.guardedUpdates - [long]$startPerf.guardedUpdates
        injectedStalls = [long]$endPerf.injectedStalls - [long]$startPerf.injectedStalls
        atlasSuppressedPages = [long]$endPerf.atlasSuppressedPages - [long]$startPerf.atlasSuppressedPages
        atlasDirtyPages = [long]$endPerf.atlasDirtyPages - [long]$startPerf.atlasDirtyPages
        workingSetBytes = $process.WorkingSet64
        privateBytes = $process.PrivateMemorySize64
        qualityVSyncCount = [int]$endPerf.qualityVSyncCount
        targetFrameRate = [int]$endPerf.targetFrameRate
        errors = [long]$endPerf.errors
        nativeAtlas = if ($nativeAtlas) { [ordered]@{
            state = [string]$nativeStatus.state
            applied = [bool]$nativeStatus.applied
            patchedSites = [int]$nativeStatus.patchedSites
            managedHotPathCallbacks = [int]$nativeStatus.managedHotPathCallbacks
            gameAssemblySha256 = [string]$nativeStatus.gameAssemblySha256
        }} else { $null }
        renderer = if ($rendererEnabled) { [ordered]@{
            renderedFrames = [long]$endRenderer.renderedFrames - [long]$startRenderer.renderedFrames
            fallbackFrames = [long]$endRenderer.fallbackFrames - [long]$startRenderer.fallbackFrames
            backgroundCacheHits = [long]$endRenderer.backgroundCacheHits - [long]$startRenderer.backgroundCacheHits
            backgroundCacheMisses = [long]$endRenderer.backgroundCacheMisses - [long]$startRenderer.backgroundCacheMisses
            rasterEffectRebuilds = [long]$endRenderer.rasterEffectRebuilds - [long]$startRenderer.rasterEffectRebuilds
            rasterPartialRebuilds = [long]$endRenderer.rasterPartialRebuilds - [long]$startRenderer.rasterPartialRebuilds
            rasterPartialRows = [long]$endRenderer.rasterPartialRows - [long]$startRenderer.rasterPartialRows
            averageRenderMs = [double]$endRenderer.averageRenderMs
            averageBackgroundMs = [double]$endRenderer.stageAverageMs.backgrounds
            maxRenderMs = [double]$endRenderer.maxRenderMs
        }} else { $null }
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $outputPath = Join-Path $OutputDirectory ("{0}-trial{1}.json" -f $Scenario,$Trial)
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    $result | ConvertTo-Json -Depth 6
    Write-Host "Saved $outputPath" -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Refresh()
        if ($process.Path -ne $exe) { throw "Refusing to stop unexpected process: $($process.Path)" }
        Stop-Process -Id $process.Id
        Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
    }
}
