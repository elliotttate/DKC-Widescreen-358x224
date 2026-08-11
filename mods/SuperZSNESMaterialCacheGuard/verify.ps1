param(
    [string]$GameAssembly = (Join-Path $env:SUPERZSNES_MANAGED_DIR 'Assembly-CSharp.dll'),
    [string]$PluginAssembly = "$PSScriptRoot\bin\Release\net472\SuperZSNESMaterialCacheGuard.dll",
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$InstalledPluginRoot = (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins')
)

$ErrorActionPreference = 'Stop'
$managedDirectory = Split-Path -Parent $GameAssembly
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

function Load-AssemblyBytes([string] $path) {
    return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
}

foreach ($path in @($GameAssembly, $PluginAssembly)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required assembly not found: $path" }
}
$gameHash = (Get-FileHash -LiteralPath $GameAssembly -Algorithm SHA256).Hash
if ($gameHash -ne $expectedGameHash) {
    throw "Assembly-CSharp hash mismatch: expected $expectedGameHash, got $gameHash"
}

Load-AssemblyBytes (Join-Path $managedDirectory 'netstandard.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.Mathematics.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'Unity.TextMeshPro.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.CoreModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UIModule.dll') | Out-Null
Load-AssemblyBytes (Join-Path $managedDirectory 'UnityEngine.UI.dll') | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll')) | Out-Null
$harmonyAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $BepInExRoot 'BepInEx\core\0Harmony.dll'))
$game = Load-AssemblyBytes $GameAssembly
$plugin = [Reflection.Assembly]::LoadFrom($PluginAssembly)

$renderer = $game.GetType('PPURenderer', $true)
$generate = $renderer.GetMethod('GenerateBackgrounds', [Reflection.BindingFlags]'Instance,Public')
$generateOne = $renderer.GetMethod('GenerateBackground', [Reflection.BindingFlags]'Instance,NonPublic')
$process = $renderer.GetMethod('ProcessMaterial', [Reflection.BindingFlags]'Instance,NonPublic')
$scratchField = $renderer.GetField('tileAddrToMat', [Reflection.BindingFlags]'Instance,NonPublic')
$listType = $scratchField.FieldType.GetGenericArguments()[1]
$tileInfoType = $listType.GetGenericArguments()[0]
if ($listType.GetGenericTypeDefinition() -ne [Collections.Generic.List``1] -or
    $tileInfoType.DeclaringType -ne $renderer -or $tileInfoType.Name -ne 'TileInfo' -or -not $tileInfoType.IsNestedPrivate) {
    throw "Unexpected tileAddrToMat value type: $($listType.FullName)"
}

$layoutType = $plugin.GetType('SuperZSNESMaterialCacheGuard.MaterialCacheLayout', $true)
$layout = $layoutType.GetMethod('ResolveAndVerify', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
if ($null -eq $layout) { throw 'MaterialCacheLayout.ResolveAndVerify returned null.' }

$patchProcessor = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getInstructions = $patchProcessor.GetMethods([Reflection.BindingFlags]'Public,Static') |
    Where-Object { $_.Name -eq 'GetOriginalInstructions' -and -not $_.GetParameters()[1].ParameterType.IsByRef }
$original = $getInstructions.Invoke($null, @($process, $null))
$transpilerType = $plugin.GetType('SuperZSNESMaterialCacheGuard.ProcessMaterialTranspiler', $true)
$transpiler = $transpilerType.GetMethod('Transpiler', [Reflection.BindingFlags]'Public,Static')
$transpilerType.GetField('TransformCount', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, 0)
$transpilerArguments = [object[]]::new(1)
$transpilerArguments[0] = $original
$transformed = @($transpiler.Invoke($null, $transpilerArguments))
$rentIndices = @()
for ($index = 0; $index -lt $transformed.Count; $index++) {
    $operand = $transformed[$index].operand
    if ($operand -is [Reflection.MethodInfo] -and
        $operand.DeclaringType.FullName -eq 'SuperZSNESMaterialCacheGuard.ScratchListPool' -and
        $operand.Name -eq 'RentObject') {
        $rentIndices += $index
    }
}
if ($rentIndices.Count -ne 1) { throw "Expected one RentObject call; found $($rentIndices.Count)." }
$rentIndex = $rentIndices[0]
if ($transformed.Count -ne $original.Count + 1 -or
    $transformed[$rentIndex + 1].opcode.Name -ne 'castclass' -or
    $transformed[$rentIndex + 1].operand -ne $listType) {
    throw 'ProcessMaterial transform did not produce call object RentObject() + castclass List<TileInfo>.'
}
if ($transpilerType.GetField('TransformCount', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null) -ne 1) {
    throw 'ProcessMaterial TransformCount was not one.'
}

$getPatchInfo = $patchProcessor.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
if ($null -ne $getPatchInfo.Invoke($null, @($process)) -or
    $null -ne $getPatchInfo.Invoke($null, @($generate)) -or
    $null -ne $getPatchInfo.Invoke($null, @($generateOne))) {
    throw 'Verifier unexpectedly patched a game method.'
}

# Exercise the real pool with the actual private List<PPURenderer.TileInfo>
# runtime type. Frame zero establishes a 64-list high-water; 2,000 varying
# backgrounds must reuse those same lists and keep the map at current-frame size.
$poolType = $plugin.GetType('SuperZSNESMaterialCacheGuard.ScratchListPool', $true)
$initialize = $poolType.GetMethod('Initialize', [Reflection.BindingFlags]'Static,NonPublic')
$harvest = $poolType.GetMethod('HarvestAndClear', [Reflection.BindingFlags]'Static,NonPublic')
$rent = $poolType.GetMethod('RentObject', [Reflection.BindingFlags]'Static,Public')
$getStats = $poolType.GetMethod('GetStats', [Reflection.BindingFlags]'Static,NonPublic')
$initArguments = [object[]]::new(2)
$initArguments[0] = $listType
$initArguments[1] = $listType.GetConstructor([Type[]]@())
$initialize.Invoke($null, $initArguments) | Out-Null
$map = [Collections.Hashtable]::new()
$highWater = 64
$allocationsAfterWarmup = 0L
$maxObservedMapCount = 0
$totalFrames = 2001
for ($frame = 0; $frame -lt $totalFrames; $frame++) {
    $harvestArguments = [object[]]::new(1)
    $harvestArguments[0] = $map
    $harvest.Invoke($null, $harvestArguments) | Out-Null
    $keys = if ($frame -eq 0) { $highWater } else { 1 + (($frame * 17) % $highWater) }
    for ($key = 0; $key -lt $keys; $key++) {
        $map[$key] = $rent.Invoke($null, @())
    }
    if ($map.Count -ne $keys) { throw "Frame $frame retained $($map.Count) entries; expected $keys." }
    if ($map.Count -gt $maxObservedMapCount) { $maxObservedMapCount = $map.Count }
    $stats = $getStats.Invoke($null, @())
    $allocated = [long]$stats.GetType().GetField('TotalAllocations', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($stats)
    if ($frame -eq 0) { $allocationsAfterWarmup = $allocated }
    elseif ($allocated -ne $allocationsAfterWarmup) {
        throw "List allocation count grew after high-water: warm=$allocationsAfterWarmup frame=$frame now=$allocated."
    }
}
$finalHarvestArguments = [object[]]::new(1)
$finalHarvestArguments[0] = $map
$harvest.Invoke($null, $finalHarvestArguments) | Out-Null
$finalStats = $getStats.Invoke($null, @())
$statsType = $finalStats.GetType()
$finalAllocations = [long]$statsType.GetField('TotalAllocations', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($finalStats)
$finalPoolCount = [int]$statsType.GetField('PoolCount', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($finalStats)
if ($finalAllocations -ne $highWater -or $finalPoolCount -ne $highWater -or $maxObservedMapCount -ne $highWater) {
    throw "Stress result mismatch: allocations=$finalAllocations pool=$finalPoolCount maxMap=$maxObservedMapCount"
}

$source = Get-Content -LiteralPath "$PSScriptRoot\SuperZSNESMaterialCacheGuardPlugin.cs" -Raw
if ($source -notmatch '"EnablePerBackgroundScratchListPool", true') {
    throw 'EnablePerBackgroundScratchListPool is not default true.'
}
if ($source -match 'EnablePeriodicScratchMapClear|ScratchMapClearIntervalRenderCalls') {
    throw 'Coarse periodic scratch clear remains in plugin source.'
}
if ($source -match 'UnityEngine\.Object\.Destroy|Resources\.UnloadUnusedAssets') {
    throw 'Guard source contains a forbidden Unity asset-destruction call.'
}

$installed = @()
if (Test-Path -LiteralPath $InstalledPluginRoot) {
    $installed = @(Get-ChildItem -LiteralPath $InstalledPluginRoot -Recurse -Filter 'SuperZSNESMaterialCacheGuard.dll' -ErrorAction SilentlyContinue |
        ForEach-Object { [pscustomobject]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}

[pscustomobject]@{
    verified = $true
    gameAssemblySha256 = $gameHash
    pluginSha256 = (Get-FileHash -LiteralPath $PluginAssembly -Algorithm SHA256).Hash
    processMaterialOriginalInstructions = $original.Count
    processMaterialTransformedInstructions = $transformed.Count
    rentReplacements = $rentIndices.Count
    runtimeMethodsPatched = $false
    stressFrames = $totalFrames
    stressHighWater = $highWater
    stressMaxScratchMapCount = $maxObservedMapCount
    stressListAllocations = $finalAllocations
    stressFinalPoolCount = $finalPoolCount
    allocationsAfterHighWater = 0
    destroysUnityAssets = $false
    defaults = [pscustomobject]@{ perBackgroundScratchListPool = $true; diagnostics = $false }
    installedCopies = $installed
} | ConvertTo-Json -Depth 5
