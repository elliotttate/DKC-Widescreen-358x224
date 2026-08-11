$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepInExRoot = if ($env:SUPERZSNES_CACHE_GATE_BEPINEX_ROOT) { $env:SUPERZSNES_CACHE_GATE_BEPINEX_ROOT } else { $env:BEPINEX_ROOT }
$managedDirectory = if ($env:SUPERZSNES_CACHE_GATE_MANAGED_DIR) { $env:SUPERZSNES_CACHE_GATE_MANAGED_DIR } else { $env:SUPERZSNES_MANAGED_DIR }
$installedRoot = if ($env:SUPERZSNES_CACHE_GATE_INSTALLED_ROOT) { $env:SUPERZSNES_CACHE_GATE_INSTALLED_ROOT } else { (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins') }
$pluginPath = Join-Path $projectDirectory 'bin\Release\net472\SuperZSNESDrawLinesCacheGate.dll'
$oldPluginPath = Join-Path $installedRoot 'SuperZSNESRendererFastPaths\SuperZSNESRendererFastPaths.dll'
$gameAssemblyPath = Join-Path $managedDirectory 'Assembly-CSharp.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'
$oldOwner = 'dev.local.superzsnes.rendererfastpaths'
$newOwner = 'dev.local.superzsnes.drawlinescachegate'

function Load-AssemblyBytes([string] $path) { return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path)) }
function First-Method([Type] $type, [string] $name, [int] $parameterCount) {
    return $type.GetMethods() | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $parameterCount } | Select-Object -First 1
}
function Count-Calls($instructions, [Reflection.MethodInfo] $method) {
    return @($instructions | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand -eq $method }).Count
}
function Original-Instructions([Reflection.MethodBase] $method, [Reflection.Emit.ILGenerator] $generator = $null) {
    $arguments = [object[]]::new(2)
    $arguments[0] = $method
    $arguments[1] = $generator
    $result = $script:getOriginal.Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function Invoke-Transpiler([Reflection.MethodInfo] $method, $instructions, [Reflection.Emit.ILGenerator] $generator = $null) {
    $count = $method.GetParameters().Count
    $arguments = [object[]]::new($count)
    $arguments[0] = $instructions.psobject.BaseObject
    if ($count -eq 2) { $arguments[1] = $generator }
    $result = $method.Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function New-Generator([string] $name) {
    $dynamicMethod = [Reflection.Emit.DynamicMethod]::new($name, [void], [Type[]]@())
    return $dynamicMethod.GetILGenerator()
}
function Patch-Transpiler($harmony, [Reflection.MethodBase] $target, $harmonyMethod) {
    $arguments = [object[]]::new(5)
    $arguments[0] = $target
    $arguments[1] = $null
    $arguments[2] = $null
    $arguments[3] = $harmonyMethod
    $arguments[4] = $null
    $script:patchMethod.Invoke($harmony, $arguments) | Out-Null
}
function Current-Instructions([Reflection.MethodBase] $method) {
    $arguments = [object[]]::new(3)
    $arguments[0] = $method
    $arguments[1] = [int]::MaxValue
    $arguments[2] = $null
    $result = $script:getCurrent.Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function Unpatch-Owner($harmony, [string] $owner) {
    $arguments = [object[]]::new(1)
    $arguments[0] = $owner
    $script:unpatchAll.Invoke($harmony, $arguments) | Out-Null
}
function Assert-GatedShape($instructions, [string] $label) {
    $tryIndices = @()
    $processIndices = @()
    $itemIndices = @()
    for ($index = 0; $index -lt $instructions.Count; $index++) {
        $operand = $instructions[$index].operand
        if ($operand -isnot [Reflection.MethodInfo]) { continue }
        if ($operand -eq $script:tryGetValue) { $tryIndices += $index }
        if ($operand -eq $script:processMaterial) { $processIndices += $index }
        if ($operand -eq $script:indexer) { $itemIndices += $index }
    }
    if ($tryIndices.Count -ne 2 -or $processIndices.Count -ne 2 -or $itemIndices.Count -ne 2 -or
        (Count-Calls $instructions $script:containsKey) -ne 0) {
        throw "$label call counts differ: TryGet=$($tryIndices.Count) Process=$($processIndices.Count) Item=$($itemIndices.Count)."
    }

    for ($site = 0; $site -lt 2; $site++) {
        $tryIndex = $tryIndices[$site]
        $processIndex = $processIndices[$site]
        $itemIndex = $itemIndices[$site]
        if ($processIndex -le $tryIndex -or $itemIndex -le $processIndex -or $processIndex - $tryIndex -gt 20 -or
            $itemIndex - $processIndex -gt 6) { throw "$label site $site is not TryGet -> Process -> indexer." }
        $branch = $instructions[$tryIndex + 1]
        if ($branch.opcode.Name -notmatch '^brtrue') { throw "$label site $site does not branch over miss handling on a hit." }
        $targetIndices = @()
        for ($candidate = 0; $candidate -lt $instructions.Count; $candidate++) {
            if ($instructions[$candidate].labels.Contains($branch.operand)) { $targetIndices += $candidate }
        }
        if ($targetIndices.Count -ne 1 -or $targetIndices[0] -ne $itemIndex + 2) {
            throw "$label site $site hit target is not the first body instruction after indexer/store (try=$tryIndex process=$processIndex item=$itemIndex targets=$($targetIndices -join ',') expected=$($itemIndex + 2))."
        }
        $outLocal = $instructions[$tryIndex - 1].operand
        $storeLocal = $instructions[$itemIndex + 1].operand
        $bodyLocal = $instructions[$targetIndices[0]].operand
        if ($outLocal -ne $storeLocal -or $outLocal -ne $bodyLocal) {
            throw "$label site $site does not retain one value local across hit, miss retrieval, and body."
        }
    }
}
function Instruction-Fingerprint($instructions) {
    $lines = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $instructions.Count; $index++) {
        $instruction = $instructions[$index]
        $operand = $instruction.operand
        $description = ''
        if ($operand -is [Reflection.MemberInfo]) {
            $description = $operand.DeclaringType.FullName + '::' + $operand.Name
        }
        elseif ($operand -is [Reflection.Emit.LocalBuilder]) {
            $description = 'local:' + $operand.LocalIndex + ':' + $operand.LocalType.FullName
        }
        elseif ($operand -is [Reflection.Emit.Label]) {
            $target = -1
            for ($candidate = 0; $candidate -lt $instructions.Count; $candidate++) {
                if ($instructions[$candidate].labels.Contains($operand)) { $target = $candidate; break }
            }
            $description = 'target:' + $target
        }
        elseif ($null -ne $operand) { $description = $operand.ToString() }
        $lines.Add($instruction.opcode.Name + '|' + $description)
    }
    return [string]::Join("`n", $lines)
}

foreach ($required in @($gameAssemblyPath, $pluginPath, $oldPluginPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required file not found: $required" }
}
$gameHash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash
if ($gameHash -ne $expectedGameHash) { throw "Assembly-CSharp hash mismatch: $gameHash" }
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
$oldPluginAssembly = [Reflection.Assembly]::LoadFrom($oldPluginPath)
$pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)

$renderer = $gameAssembly.GetType('PPURenderer', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$drawLines = $renderer.GetMethod('DrawLines', $flags)
$process = $renderer.GetMethod('ProcessMaterial', $flags)
$cacheField = $renderer.GetField('matDict', $flags)
$dictionaryType = $cacheField.FieldType
$script:processMaterial = $process
$script:containsKey = First-Method $dictionaryType 'ContainsKey' 1
$script:indexer = $dictionaryType.GetProperty('Item').GetGetMethod()
$script:tryGetValue = First-Method $dictionaryType 'TryGetValue' 2

$patchProcessor = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$script:getOriginal = $patchProcessor.GetMethods([Reflection.BindingFlags]'Public,Static') |
    Where-Object { $_.Name -eq 'GetOriginalInstructions' -and $_.GetParameters().Count -eq 2 -and -not $_.GetParameters()[1].ParameterType.IsByRef } |
    Select-Object -First 1
$script:getCurrent = $patchProcessor.GetMethods([Reflection.BindingFlags]'Public,Static') |
    Where-Object { $_.Name -eq 'GetCurrentInstructions' -and $_.GetParameters().Count -eq 3 -and $_.GetParameters()[1].ParameterType -eq [int] } |
    Select-Object -First 1
$getPatchInfo = $patchProcessor.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
$initialPatchInfo = $getPatchInfo.Invoke($null, @($drawLines))
if ($null -ne $initialPatchInfo -and $initialPatchInfo.Transpilers.Count -ne 0) { throw 'Verifier process started with DrawLines unexpectedly patched.' }

$newType = $pluginAssembly.GetType('SuperZSNESDrawLinesCacheGate.CacheGateOptimization', $true)
$newTranspiler = $newType.GetMethod('Transpiler', [Reflection.BindingFlags]'Static,Public')
$createHarmonyMethod = $newType.GetMethod('CreateHarmonyMethod', [Reflection.BindingFlags]'Static,NonPublic')
$oldType = $oldPluginAssembly.GetType('SuperZSNESRendererFastPaths.DrawLinesMaterialLookupOptimization', $true)
$oldTranspiler = $oldType.GetMethod('Transpiler', [Reflection.BindingFlags]'Static,Public')
# Direct stock input support.
$stockGenerator = New-Generator 'verify_stock_gate'
$stockInput = Original-Instructions $drawLines $stockGenerator
$stockOutput = Invoke-Transpiler $newTranspiler $stockInput $stockGenerator
Assert-GatedShape $stockOutput 'stock-input transform'

# Direct RendererFastPaths-normalized input support.
$normalizedGenerator = New-Generator 'verify_normalized_gate'
$normalizedInput = Original-Instructions $drawLines $normalizedGenerator
$normalized = Invoke-Transpiler $oldTranspiler $normalizedInput
$normalizedOutput = Invoke-Transpiler $newTranspiler $normalized $normalizedGenerator
Assert-GatedShape $normalizedOutput 'normalized-input transform'

# Exact ProcessMaterial proof: once TryGetValue established a miss, both non-throwing miss exits insert matDict.
$processInstructions = Original-Instructions $process (New-Generator 'verify_process')
$matAdds = @()
$returns = @()
$matAddMethod = First-Method $dictionaryType 'Add' 2
for ($index = 0; $index -lt $processInstructions.Count; $index++) {
    if ($processInstructions[$index].operand -eq $matAddMethod) { $matAdds += $index }
    if ($processInstructions[$index].opcode.Name -eq 'ret') { $returns += $index }
}
if ($matAdds.Count -ne 2 -or $returns.Count -ne 2 -or $matAdds[0] + 1 -ne $returns[0] -or
    $matAdds[1] -ge $returns[1] -or $processInstructions[0].opcode.Name -ne 'ldarg.0' -or
    $processInstructions[3].operand -ne $script:containsKey) {
    throw 'ProcessMaterial exact insert-on-miss proof shape changed.'
}

# Register the real two-owner Harmony chain in both orders. The new HarmonyMethod has an explicit `after` owner.
$harmonyType = $harmonyAssembly.GetType('HarmonyLib.Harmony', $true)
$harmonyMethodType = $harmonyAssembly.GetType('HarmonyLib.HarmonyMethod', $true)
$harmonyConstructor = $harmonyType.GetConstructor([Type[]]@([string]))
$harmonyMethodConstructor = $harmonyMethodType.GetConstructor([Type[]]@([Reflection.MethodInfo]))
$script:patchMethod = $harmonyType.GetMethods() | Where-Object { $_.Name -eq 'Patch' -and $_.GetParameters().Count -eq 5 } | Select-Object -First 1
$script:unpatchAll = $harmonyType.GetMethod('UnpatchAll', [Type[]]@([string]))
$oldHarmony = $harmonyConstructor.Invoke(@($oldOwner))
$newHarmony = $harmonyConstructor.Invoke(@($newOwner))
$oldHarmonyMethod = $harmonyMethodConstructor.Invoke(@($oldTranspiler))
$newHarmonyMethod = $createHarmonyMethod.Invoke($null, @())
if ($newHarmonyMethod.after.Count -ne 1 -or $newHarmonyMethod.after[0] -ne $oldOwner) {
    throw 'New transpiler does not carry the required Harmony-after owner constraint.'
}

Patch-Transpiler $oldHarmony $drawLines $oldHarmonyMethod
Patch-Transpiler $newHarmony $drawLines $newHarmonyMethod
$oldThenNew = Current-Instructions $drawLines
Assert-GatedShape $oldThenNew 'live chain old-then-new registration'
$fingerprintA = Instruction-Fingerprint $oldThenNew
Unpatch-Owner $newHarmony $newOwner
Unpatch-Owner $oldHarmony $oldOwner
$afterFirstUnpatch = $getPatchInfo.Invoke($null, @($drawLines))
if ($null -ne $afterFirstUnpatch -and $afterFirstUnpatch.Transpilers.Count -ne 0) { throw 'First live-chain run did not unpatch cleanly.' }

Patch-Transpiler $newHarmony $drawLines $newHarmonyMethod
Patch-Transpiler $oldHarmony $drawLines $oldHarmonyMethod
$newThenOld = Current-Instructions $drawLines
Assert-GatedShape $newThenOld 'live chain new-then-old registration'
$fingerprintB = Instruction-Fingerprint $newThenOld
if ($fingerprintA -ne $fingerprintB) { throw 'Harmony registration orders produced different final DrawLines instruction chains.' }
Unpatch-Owner $newHarmony $newOwner
Unpatch-Owner $oldHarmony $oldOwner
$afterSecondUnpatch = $getPatchInfo.Invoke($null, @($drawLines))
if ($null -ne $afterSecondUnpatch -and $afterSecondUnpatch.Transpilers.Count -ne 0) { throw 'Second live-chain run did not unpatch cleanly.' }

# Semantic model covers non-null/null hits, hiRes behavior, and both miss insertions.
function Current-Model([Collections.Generic.Dictionary[string,string]] $map, [string] $key, [bool] $hiRes, [bool] $secondPath, [string] $insert, [ref] $calls) {
    $calls.Value++
    if (-not $map.ContainsKey($key)) { $map.Add($key, $insert) }
    if (-not $map.ContainsKey($key)) { return $false }
    $value = $map[$key]
    if ($secondPath) { return $null -ne $value }
    return $null -ne $value -or $hiRes
}
function Gated-Model([Collections.Generic.Dictionary[string,string]] $map, [string] $key, [bool] $hiRes, [bool] $secondPath, [string] $insert, [ref] $calls) {
    $value = $null
    if (-not $map.TryGetValue($key, [ref]$value)) {
        $calls.Value++
        if (-not $map.ContainsKey($key)) { $map.Add($key, $insert) }
        $value = $map[$key]
    }
    if ($secondPath) { return $null -ne $value }
    return $null -ne $value -or $hiRes
}
$cases = @(
    @{ Hit=$true; Value='material'; Hi=$false; Second=$false },
    @{ Hit=$true; Value=$null; Hi=$false; Second=$false },
    @{ Hit=$true; Value=$null; Hi=$true; Second=$false },
    @{ Hit=$true; Value=$null; Hi=$true; Second=$true },
    @{ Hit=$false; Value='material'; Hi=$false; Second=$false },
    @{ Hit=$false; Value=$null; Hi=$true; Second=$false },
    @{ Hit=$false; Value=$null; Hi=$true; Second=$true }
)
foreach ($case in $cases) {
    $mapA = [Collections.Generic.Dictionary[string,string]]::new()
    $mapB = [Collections.Generic.Dictionary[string,string]]::new()
    if ($case.Hit) { $mapA.Add('key', $case.Value); $mapB.Add('key', $case.Value) }
    $callsA = 0
    $callsB = 0
    $resultA = Current-Model $mapA 'key' $case.Hi $case.Second $case.Value ([ref]$callsA)
    $resultB = Gated-Model $mapB 'key' $case.Hi $case.Second $case.Value ([ref]$callsB)
    if ($resultA -ne $resultB -or $mapA.Count -ne $mapB.Count -or $mapA['key'] -ne $mapB['key'] -or
        ($case.Hit -and $callsB -ne 0) -or (-not $case.Hit -and $callsB -ne 1)) {
        throw "Semantic model mismatch: $($case | ConvertTo-Json -Compress)"
    }
}

$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'SuperZSNESDrawLinesCacheGatePlugin.cs') -Raw
if ($source -notmatch '"Optimization", "Enabled", false' -or $source -notmatch 'BepInDependency\(RendererFastPathsGuid') {
    throw 'Disabled default or soft dependency contract is missing.'
}
$installedCopies = @()
if (Test-Path -LiteralPath $installedRoot) {
    $installedCopies = @(Get-ChildItem -LiteralPath $installedRoot -Recurse -Filter 'SuperZSNESDrawLinesCacheGate.dll' -ErrorAction SilentlyContinue)
}
if ($installedCopies.Count -ne 0) { throw "Cache-gate DLL is unexpectedly installed: $($installedCopies.FullName -join ', ')" }

$pluginHash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
$oldPluginHash = (Get-FileHash -LiteralPath $oldPluginPath -Algorithm SHA256).Hash
Write-Output 'PASS: stock and RendererFastPaths-normalized DrawLines inputs each produce exactly two cache gates.'
Write-Output 'PASS: each gate is TryGetValue -> hit branch, or ProcessMaterial -> one inserted-value indexer on miss.'
Write-Output 'PASS: exact ProcessMaterial IL inserts matDict on both non-throwing miss exits, including null entries.'
Write-Output 'PASS: actual two-owner Harmony chain is identical in both transpiler registration orders.'
Write-Output 'PASS: null/hiRes/second-path semantic model matches current behavior and skips ProcessMaterial only on hits.'
Write-Output 'PASS: plugin is disabled by default, declares the soft dependency/after ordering, and is not installed.'
Write-Output "Assembly-CSharp SHA256: $gameHash"
Write-Output "Existing RendererFastPaths v0.1 SHA256: $oldPluginHash"
Write-Output "DrawLines cache-gate DLL SHA256: $pluginHash"
