$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepInExRoot = if ($env:SUPERZSNES_PAUSE_PROBE_BEPINEX_ROOT) {
    $env:SUPERZSNES_PAUSE_PROBE_BEPINEX_ROOT
} else {
    $env:BEPINEX_ROOT
}
$managedDirectory = if ($env:SUPERZSNES_PAUSE_PROBE_MANAGED_DIR) {
    $env:SUPERZSNES_PAUSE_PROBE_MANAGED_DIR
} else {
    $env:SUPERZSNES_MANAGED_DIR
}
$pluginPath = Join-Path $projectDirectory 'bin\Release\net472\SuperZSNESRuntimePauseProbe.dll'
$gameAssemblyPath = Join-Path $managedDirectory 'Assembly-CSharp.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

function Load-AssemblyBytes([string] $path) {
    return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
}

$actualGameHash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash
if ($actualGameHash -ne $expectedGameHash) {
    throw "Assembly-CSharp.dll hash mismatch: expected $expectedGameHash, got $actualGameHash"
}

Load-AssemblyBytes (Join-Path $managedDirectory 'netstandard.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.Mathematics.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.TextMeshPro.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.CoreModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UIModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UI.dll') | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $bepInExRoot 'BepInEx\core\BepInEx.dll')) | Out-Null
$harmonyAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bepInExRoot 'BepInEx\core\0Harmony.dll'))
$gameAssembly = Load-AssemblyBytes $gameAssemblyPath
$pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)

$pluginType = $pluginAssembly.GetType('SuperZSNESRuntimePauseProbe.SuperZSNESRuntimePauseProbePlugin', $true)
if ($pluginType.GetField('PluginVersion').GetRawConstantValue() -ne '0.1.0') {
    throw 'Unexpected plugin version.'
}

$masterType = $gameAssembly.GetType('MasterExecutor', $true)
$overlayType = $gameAssembly.GetType('SaveStateSelectOverlay', $true)
$targets = @(
    $masterType.GetMethod('Update', [Reflection.BindingFlags]'Instance,NonPublic'),
    $masterType.GetMethod('RunFrame', [Reflection.BindingFlags]'Instance,NonPublic'),
    $masterType.GetMethod('PauseGame', [Reflection.BindingFlags]'Instance,Public'),
    $masterType.GetMethod('ResumeGame', [Reflection.BindingFlags]'Instance,Public'),
    $masterType.GetMethod('StepFrameForward', [Reflection.BindingFlags]'Instance,Public'),
    $masterType.GetMethod('ReturnToGame', [Reflection.BindingFlags]'Instance,Public'),
    $masterType.GetMethod('EscapeBackToMenu', [Reflection.BindingFlags]'Instance,Public'),
    $overlayType.GetMethod('OnEnable', [Reflection.BindingFlags]'Instance,NonPublic'),
    $overlayType.GetMethod('OnDisable', [Reflection.BindingFlags]'Instance,NonPublic')
)
if (@($targets | Where-Object { $null -eq $_ }).Count -ne 0) {
    throw 'One or more expected v0.230 diagnostic targets were not found.'
}

$patchProcessorType = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getPatchInfo = $patchProcessorType.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
foreach ($target in $targets) {
    if ($null -ne $getPatchInfo.Invoke($null, @($target))) {
        throw "Verifier unexpectedly found target patched: $($target.DeclaringType.FullName).$($target.Name)"
    }
}

$runtimeType = $pluginAssembly.GetType('SuperZSNESRuntimePauseProbe.ProbeRuntime', $true)
$thresholdField = $runtimeType.GetField('_thresholdTicks', [Reflection.BindingFlags]'Static,NonPublic')
$frequency = [Diagnostics.Stopwatch]::Frequency
$thresholdField.SetValue($null, [long]($frequency * 0.100))
$classify = $runtimeType.GetMethod('ClassifyGap', [Reflection.BindingFlags]'Static,NonPublic')
function Classify([double] $runMs, [double] $updateMs, [double] $watchdogMs, [long] $gated, [int] $gc0, [int] $gc1, [int] $gc2) {
    return $classify.Invoke($null, @(
        [long]($frequency * $runMs / 1000),
        [long]($frequency * $updateMs / 1000),
        [long]($frequency * $watchdogMs / 1000),
        $gated, $gc0, $gc1, $gc2))
}
if ((Classify 950 30 25 80 0 0 0) -ne 'emulation-gated') { throw 'Gate classification failed.' }
if ((Classify 950 30 25 0 0 0 0) -ne 'scheduler-no-frame-with-updates') { throw 'Scheduler classification failed.' }
if ((Classify 950 900 25 0 0 0 0) -ne 'unity-main-thread-stall') { throw 'Main-thread classification failed.' }
if ((Classify 950 900 850 0 3 0 0) -ne 'runtime-wide-pause-with-gc') { throw 'GC classification failed.' }
if ((Classify 950 900 850 0 0 0 0) -ne 'runtime-wide-pause-or-process-deschedule') { throw 'Runtime-wide classification failed.' }

$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'SuperZSNESRuntimePauseProbePlugin.cs') -Raw
if ($source -notmatch '"Probe", "Enabled", false') {
    throw 'Probe.Enabled is not disabled by default.'
}
if ($source -notmatch 'no watcher thread was started and no target methods were patched') {
    throw 'Disabled-mode contract text is missing.'
}

$pluginHash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
Write-Output 'PASS: plugin is disabled by default and its disabled path declares no watcher/patch activity.'
Write-Output 'PASS: all nine actual SuperZSNES v0.230 diagnostic targets exist.'
Write-Output 'PASS: verifier loaded and inspected the targets without patching them.'
Write-Output 'PASS: synthetic gate, scheduler, main-thread, runtime+GC, and runtime-deschedule classifications match.'
Write-Output "Assembly-CSharp SHA256: $actualGameHash"
Write-Output "Probe DLL SHA256: $pluginHash"
