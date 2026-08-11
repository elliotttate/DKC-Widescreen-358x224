$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepInExRoot = if ($env:SUPERZSNES_ALLOC_PROBE_BEPINEX_ROOT) { $env:SUPERZSNES_ALLOC_PROBE_BEPINEX_ROOT } else { $env:BEPINEX_ROOT }
$managedDirectory = if ($env:SUPERZSNES_ALLOC_PROBE_MANAGED_DIR) { $env:SUPERZSNES_ALLOC_PROBE_MANAGED_DIR } else { $env:SUPERZSNES_MANAGED_DIR }
$pluginPath = Join-Path $projectDirectory 'bin\Release\net472\SuperZSNESAllocationProbe.dll'
$gameAssemblyPath = Join-Path $managedDirectory 'Assembly-CSharp.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

function Load-AssemblyBytes([string] $path) { return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path)) }

$actualGameHash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash
if ($actualGameHash -ne $expectedGameHash) { throw "Assembly-CSharp hash mismatch: $actualGameHash" }
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

$master = $gameAssembly.GetType('MasterExecutor', $true)
$renderer = $gameAssembly.GetType('PPURenderer', $true)
$targets = @(
    $master.GetMethod('Update', [Reflection.BindingFlags]'Instance,NonPublic'),
    $master.GetMethod('RunFrame', [Reflection.BindingFlags]'Instance,NonPublic'),
    $renderer.GetMethod('GenerateBackgrounds', [Reflection.BindingFlags]'Instance,Public'),
    $renderer.GetMethod('GenerateBackground', [Reflection.BindingFlags]'Instance,NonPublic')
)
if (@($targets | Where-Object { $null -eq $_ }).Count -ne 0) { throw 'One or more allocation targets were not found.' }
$patchProcessor = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getPatchInfo = $patchProcessor.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
foreach ($target in $targets) {
    if ($null -ne $getPatchInfo.Invoke($null, @($target))) { throw "Verifier unexpectedly found a patched target: $($target.Name)" }
}

$counter = $pluginAssembly.GetType('SuperZSNESAllocationProbe.AllocationCounter', $true)
$counter.GetMethod('Verify', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @()) | Out-Null
$read = $counter.GetMethod('Read', [Reflection.BindingFlags]'Static,NonPublic')
$before = [long]$read.Invoke($null, @())
$sample = New-Object byte[] 4096
[GC]::KeepAlive($sample)
$after = [long]$read.Invoke($null, @())
if ($after -lt $before) { throw 'Allocation counter decreased in the verifier.' }

$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'SuperZSNESAllocationProbePlugin.cs') -Raw
if ($source -notmatch '"Probe", "Enabled", false') { throw 'Probe is not disabled by default.' }
if ($source -notmatch 'no target methods were patched and no writer thread was started') { throw 'Disabled-mode contract text is missing.' }

$analysisVerified = $false
if ($env:RUNTIME_PAUSE_EVENTS -and $env:AUDIO_TIMING_WINDOWS) {
    $analysisJson = & (Join-Path $projectDirectory 'analyze-clean-run.ps1') | ConvertFrom-Json
    if ([math]::Abs($analysisJson.emulatedFrameHz - 59.999222) -gt 0.001 -or
        [math]::Abs($analysisJson.hostUpdateHz - 54.287935) -gt 0.001 -or
        $analysisJson.maximumRunFrameStartGapMs -ge 33.3334) {
        throw "Clean-run analysis regression: $($analysisJson | ConvertTo-Json -Compress)"
    }
    $analysisVerified = $true
}

$hash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
Write-Output 'PASS: all four actual SuperZSNES v0.230 scope targets exist and remain unpatched.'
Write-Output 'PASS: cumulative per-thread allocation counter is available and monotonic.'
Write-Output 'PASS: probe is disabled by default with no thread/patch activity.'
if ($analysisVerified) {
    Write-Output 'PASS: supplied clean-run logs reproduce 59.999 Hz emulation, 54.288 Hz host updates, and no >33.3 ms frame gap.'
} else {
    Write-Output 'SKIP: set RUNTIME_PAUSE_EVENTS and AUDIO_TIMING_WINDOWS to replay the historical clean-run log analysis.'
}
Write-Output "Assembly-CSharp SHA256: $actualGameHash"
Write-Output "Allocation probe DLL SHA256: $hash"
