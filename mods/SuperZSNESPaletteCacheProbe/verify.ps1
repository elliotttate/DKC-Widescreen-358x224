$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = $env:SUPERZSNES_MANAGED_DIR
$bepInEx = (Join-Path $env:BEPINEX_ROOT 'BepInEx\core')
$game = Join-Path $managed 'Assembly-CSharp.dll'
$dll = Join-Path $root 'bin\Release\net472\SuperZSNESPaletteCacheProbe.dll'
$expectedHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

if ((Get-FileHash -LiteralPath $game -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Assembly-CSharp.dll hash mismatch.' }
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Probe DLL was not built.' }

$source = Get-Content -LiteralPath (Join-Path $root 'SuperZSNESPaletteCacheProbePlugin.cs') -Raw
if ($source -notmatch '"Probe", "Enabled", false') { throw 'Probe is not disabled by default.' }
if ($source -notmatch 'no target methods were patched and no output file was opened') { throw 'Disabled-mode contract is missing.' }

foreach ($dependency in @('netstandard.dll', 'Unity.Mathematics.dll', 'Unity.TextMeshPro.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll')) {
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $managed $dependency))) | Out-Null
}
[Reflection.Assembly]::LoadFrom((Join-Path $bepInEx 'BepInEx.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $bepInEx '0Harmony.dll')) | Out-Null
$gameAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($game))
$tile = $gameAssembly.GetType('TileTextureGen', $true)
$targets = @(
    $tile.GetMethod('CalculatePalTexture', [Reflection.BindingFlags]'Instance,Public'),
    $tile.GetMethod('GenerateTextures', [Reflection.BindingFlags]'Instance,Public'),
    $tile.GetMethod('ClearCache', [Reflection.BindingFlags]'Instance,Public')
)
if (@($targets | Where-Object { $null -eq $_ }).Count -ne 0) { throw 'An expected v0.230 target is missing.' }

$pluginAssembly = [Reflection.Assembly]::LoadFrom($dll)
$counters = $pluginAssembly.GetType('SuperZSNESPaletteCacheProbe.ProbeCounters', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$counters.GetMethod('Reset', $flags).Invoke($null, @()) | Out-Null
$counters.GetMethod('Calculate', $flags).Invoke($null, @([int]4, [int]5, [long]100)) | Out-Null
$counters.GetMethod('Calculate', $flags).Invoke($null, @([int]5, [int]5, [long]50)) | Out-Null
$counters.GetMethod('Generate', $flags).Invoke($null, @([int]5, [int]4, [long]200)) | Out-Null
$counters.GetMethod('Clear', $flags).Invoke($null, @([int]4, [int]0, [long]300)) | Out-Null
$snapshot = $counters.GetMethod('Take', $flags).Invoke($null, @())
$json = $snapshot.GetType().GetMethod('ToJson').Invoke($snapshot, @([DateTime]::UtcNow, [double]5, 'test')) | ConvertFrom-Json
if ($json.calculate.calls -ne 2 -or $json.calculate.misses -ne 1 -or $json.generate.evictions -ne 1 -or $json.clear.calls -ne 1) {
    throw 'Synthetic counter aggregation failed.'
}
if ($json.cache.min -ne 0 -or $json.cache.max -ne 5 -or $json.cache.end -ne 0) { throw 'Synthetic cache extrema failed.' }

Write-Output 'PASS: shipped Assembly-CSharp.dll hash and all three targets match.'
Write-Output 'PASS: probe defaults disabled and declares no patches/output in disabled mode.'
Write-Output 'PASS: synthetic hit/miss/eviction/clear aggregation produced valid JSON and cache extrema.'
Write-Output "Probe DLL SHA256: $((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
