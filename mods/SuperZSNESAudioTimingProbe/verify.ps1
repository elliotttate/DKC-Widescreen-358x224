$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepInExRoot = if ($env:SUPERZSNES_PROBE_BEPINEX_ROOT) {
    $env:SUPERZSNES_PROBE_BEPINEX_ROOT
} else {
    $env:BEPINEX_ROOT
}
$managedDirectory = if ($env:SUPERZSNES_PROBE_MANAGED_DIR) {
    $env:SUPERZSNES_PROBE_MANAGED_DIR
} else {
    $env:SUPERZSNES_MANAGED_DIR
}
$pluginPath = Join-Path $projectDirectory 'bin\Release\net472\SuperZSNESAudioTimingProbe.dll'
$gameAssemblyPath = Join-Path $managedDirectory 'Assembly-CSharp.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

function Load-AssemblyBytes([string] $path) {
    return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
}

$actualGameHash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash
if ($actualGameHash -ne $expectedGameHash) {
    throw "Assembly-CSharp.dll hash mismatch: expected $expectedGameHash, got $actualGameHash"
}

# Loading Unity's netstandard facade from bytes avoids changing downloaded-file
# zone metadata. This process only decodes IL; it never invokes or patches a game method.
Load-AssemblyBytes (Join-Path $managedDirectory 'netstandard.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.Mathematics.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.TextMeshPro.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.CoreModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.InputLegacyModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UIModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UI.dll') | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $bepInExRoot 'BepInEx\core\BepInEx.dll')) | Out-Null
$harmonyAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bepInExRoot 'BepInEx\core\0Harmony.dll'))
$gameAssembly = Load-AssemblyBytes $gameAssemblyPath
$pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)

$audioType = $gameAssembly.GetType('DSPAudio', $true)
$audioCycle = $audioType.GetMethod('AudioCycle', [Reflection.BindingFlags]'Instance,NonPublic')
$patchProcessorType = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getInstructions = $patchProcessorType.GetMethods([Reflection.BindingFlags]'Public,Static') |
    Where-Object {
        $_.Name -eq 'GetOriginalInstructions' -and
        -not $_.GetParameters()[1].ParameterType.IsByRef
    }
$original = $getInstructions.Invoke($null, @($audioCycle, $null))

$transpilerType = $pluginAssembly.GetType('SuperZSNESAudioTimingProbe.AudioCycleLockTranspiler', $true)
$transpiler = $transpilerType.GetMethod('Transpiler', [Reflection.BindingFlags]'Public,Static')
$arguments = [object[]]::new(1)
$arguments[0] = $original
$transformed = @($transpiler.Invoke($null, $arguments))
$replacementCalls = @($transformed | Where-Object {
    $_.operand -is [Reflection.MethodInfo] -and
    $_.operand.DeclaringType.FullName -eq 'SuperZSNESAudioTimingProbe.ProbeHooks'
})
$expectedNames = @('EnterVoiceClear', 'EnterKeyOn', 'EnterKeyOnStart', 'EnterOutputCommit')
if ($replacementCalls.Count -ne 4) {
    throw "Expected four replacement calls, got $($replacementCalls.Count)."
}
for ($index = 0; $index -lt 4; $index++) {
    if ($replacementCalls[$index].operand.Name -ne $expectedNames[$index]) {
        throw "Replacement $index was $($replacementCalls[$index].operand.Name), expected $($expectedNames[$index])."
    }
}
$transformCount = $transpilerType.GetField('TransformCount', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
if ($transformCount -ne 4) { throw "TransformCount was $transformCount, expected four." }

$masterType = $gameAssembly.GetType('MasterExecutor', $true)
$masterUpdate = $masterType.GetMethod('Update', [Reflection.BindingFlags]'Instance,NonPublic')
$updateOriginal = $getInstructions.Invoke($null, @($masterUpdate, $null))
$updateTranspilerType = $pluginAssembly.GetType('SuperZSNESAudioTimingProbe.UpdateSchedulerTranspiler', $true)
$updateTranspiler = $updateTranspilerType.GetMethod('Transpiler', [Reflection.BindingFlags]'Public,Static')
$updateArguments = [object[]]::new(1)
$updateArguments[0] = $updateOriginal
$updateTransformed = @($updateTranspiler.Invoke($null, $updateArguments))
$schedulerCalls = @($updateTransformed | Where-Object {
    $_.operand -is [Reflection.MethodInfo] -and
    $_.operand.DeclaringType.FullName -eq 'SuperZSNESAudioTimingProbe.ProbeRuntime' -and
    $_.operand.Name -eq 'RecordSchedulerDecision'
})
if ($schedulerCalls.Count -ne 1) {
    throw "Expected one MasterExecutor.Update scheduler decision call, got $($schedulerCalls.Count)."
}
$updateTransformCount = $updateTranspilerType.GetField('TransformCount', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
if ($updateTransformCount -ne 1) { throw "Update TransformCount was $updateTransformCount, expected one." }

$hooksType = $pluginAssembly.GetType('SuperZSNESAudioTimingProbe.ProbeHooks', $true)
$timedEnter = $hooksType.GetMethod('TimedEnter', [Reflection.BindingFlags]'Static,NonPublic')
$wrapperInstructions = $getInstructions.Invoke($null, @($timedEnter, $null))
$wrapperCalls = @($wrapperInstructions | Where-Object { $_.operand -is [Reflection.MethodInfo] })
$tryEnterCount = @($wrapperCalls | Where-Object {
    $_.operand.DeclaringType -eq [Threading.Monitor] -and $_.operand.Name -eq 'TryEnter'
}).Count
$enterCount = @($wrapperCalls | Where-Object {
    $_.operand.DeclaringType -eq [Threading.Monitor] -and $_.operand.Name -eq 'Enter'
}).Count
$timestampCount = @($wrapperCalls | Where-Object {
    $_.operand.DeclaringType -eq [Diagnostics.Stopwatch] -and $_.operand.Name -eq 'GetTimestamp'
}).Count
if ($tryEnterCount -ne 1 -or $enterCount -ne 2 -or $timestampCount -ne 2) {
    throw "Unexpected TimedEnter call shape: TryEnter=$tryEnterCount Enter=$enterCount GetTimestamp=$timestampCount"
}

$runtimeType = $pluginAssembly.GetType('SuperZSNESAudioTimingProbe.ProbeRuntime', $true)
$runtimeType.GetMethod('ResetAll', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @()) | Out-Null
$runtimeType.GetMethod('SetCollecting', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @($true)) | Out-Null
$frequency = [Diagnostics.Stopwatch]::Frequency
$base = [long]1000000000
$starts = [long[]]@(
    $base,
    [long]($base + [long]($frequency * 0.030)),
    [long]($base + [long]($frequency * 0.070)),
    [long]($base + [long]($frequency * 0.130)),
    [long]($base + [long]($frequency * 0.250))
)
$runtimeType.GetMethod('MasterUpdateStarted', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @($base)) | Out-Null
foreach ($start in $starts) {
    $startArguments = [object[]]::new(1)
    $startArguments[0] = [long]$start
    $runtimeType.GetMethod('RunFrameStarted', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, $startArguments) | Out-Null
}
$finishArguments = [object[]]::new(2)
$finishArguments[0] = $base
$finishArguments[1] = $base + [long]($frequency * 0.255)
$runtimeType.GetMethod('MasterUpdateFinished', [Reflection.BindingFlags]'Static,NonPublic').Invoke(
    $null, $finishArguments) | Out-Null
$schedulerArguments = [object[]]::new(4)
$schedulerArguments[0] = [single]0.12
$schedulerArguments[1] = [single]60
$schedulerArguments[2] = 7
$schedulerArguments[3] = 5
$runtimeType.GetMethod('RecordSchedulerDecision', [Reflection.BindingFlags]'Static,NonPublic').Invoke(
    $null, $schedulerArguments) | Out-Null
$snapshot = $runtimeType.GetMethod('SnapshotAndReset', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @('verify'))
$snapshotType = $snapshot.GetType()
$json = $snapshotType.GetMethod('ToJson', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($snapshot, @())
$csv = $snapshotType.GetMethod('ToCsv', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($snapshot, @())
$parsed = $json | ConvertFrom-Json
if ($parsed.hostUpdate.runFramesPerUpdate.'5Plus' -ne 1 -or
    $parsed.hostUpdate.frameStartGaps.gt25Ms -ne 4 -or
    $parsed.hostUpdate.frameStartGaps.gt33_3Ms -ne 3 -or
    $parsed.hostUpdate.frameStartGaps.gt50Ms -ne 2 -or
    $parsed.hostUpdate.frameStartGaps.gt100Ms -ne 1 -or
    $parsed.hostUpdate.frameStartGaps.maxConsecutiveGt25Ms -ne 4 -or
    $parsed.hostUpdate.scheduler.droppedFrames -ne 2 -or
    $parsed.hostUpdate.scheduler.maxDroppedPerUpdate -ne 2) {
    throw "Synthetic host-gap/update-batch/scheduler metrics did not match expected values: $json"
}
$csvHeader = $snapshotType.GetField('CsvHeader', [Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
if ($csvHeader.Split(',').Length -ne $csv.Split(',').Length) {
    throw "CSV header/value mismatch: $($csvHeader.Split(',').Length) headers, $($csv.Split(',').Length) values."
}

$getPatchInfo = $patchProcessorType.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
if ($null -ne $getPatchInfo.Invoke($null, @($audioCycle))) {
    throw 'Verifier unexpectedly found AudioCycle patched in this isolated process.'
}
if ($null -ne $getPatchInfo.Invoke($null, @($masterUpdate))) {
    throw 'Verifier unexpectedly found MasterExecutor.Update patched in this isolated process.'
}

$pluginHash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
Write-Output 'PASS: actual v0.230 AudioCycle IL produced four ordered, signature-compatible lock replacements.'
Write-Output 'PASS: target method remained unpatched in the verifier process.'
Write-Output 'PASS: actual v0.230 Update IL accepted exactly one validated due/cap/drop instrumentation site.'
Write-Output 'PASS: hot wrapper has one TryEnter; Stopwatch surrounds only the blocking path.'
Write-Output 'PASS: JSON parses and CSV header/value column counts match.'
Write-Output 'PASS: synthetic gap thresholds, five-frame update batch, consecutive-gap burst, and 7-due/5-cap drop metrics match.'
Write-Output "Assembly-CSharp SHA256: $actualGameHash"
Write-Output "Probe DLL SHA256: $pluginHash"
