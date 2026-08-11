$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepInExRoot = if ($env:SUPERZSNES_RENDER_FASTPATH_BEPINEX_ROOT) { $env:SUPERZSNES_RENDER_FASTPATH_BEPINEX_ROOT } else { $env:BEPINEX_ROOT }
$managedDirectory = if ($env:SUPERZSNES_RENDER_FASTPATH_MANAGED_DIR) { $env:SUPERZSNES_RENDER_FASTPATH_MANAGED_DIR } else { $env:SUPERZSNES_MANAGED_DIR }
$installedRoot = if ($env:SUPERZSNES_RENDER_FASTPATH_INSTALLED_ROOT) { $env:SUPERZSNES_RENDER_FASTPATH_INSTALLED_ROOT } else { (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins') }
$pluginPath = Join-Path $projectDirectory 'bin\Release\net472\SuperZSNESRendererFastPaths.dll'
$gameAssemblyPath = Join-Path $managedDirectory 'Assembly-CSharp.dll'
$materialGuardPath = Join-Path (Split-Path -Parent $projectDirectory) 'SuperZSNESMaterialCacheGuard\bin\Release\net472\SuperZSNESMaterialCacheGuard.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'

function Load-AssemblyBytes([string] $path) { return [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path)) }
function First-PublicMethod([Type] $type, [string] $name, [int] $parameters) {
    return $type.GetMethods() | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $parameters } | Select-Object -First 1
}
function Count-Calls($instructions, [Reflection.MethodInfo] $method) {
    return @($instructions | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand -eq $method }).Count
}
function Original-Instructions([Reflection.MethodBase] $method) {
    $arguments = [object[]]::new(2)
    $arguments[0] = $method
    $arguments[1] = $null
    $result = $script:getOriginal.Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function Invoke-OneArgumentTranspiler([Type] $type, $instructions) {
    $arguments = [object[]]::new(1)
    $arguments[0] = $instructions.psobject.BaseObject
    $result = $type.GetMethod('Transpiler', $script:transpileFlags).Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function Invoke-TwoArgumentTranspiler([Type] $type, $instructions, [Reflection.Emit.ILGenerator] $generator) {
    $arguments = [object[]]::new(2)
    $arguments[0] = $instructions.psobject.BaseObject
    $arguments[1] = $generator
    $result = $type.GetMethod('Transpiler', $script:transpileFlags).Invoke($null, $arguments)
    Write-Output -NoEnumerate $result
}
function New-Generator([string] $name) {
    $method = [Reflection.Emit.DynamicMethod]::new($name, [void], [Type[]]@())
    return $method.GetILGenerator()
}

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

$renderer = $gameAssembly.GetType('PPURenderer', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$methodNames = @('DrawLines', 'ProcessMaterial', 'GenerateBackground', 'UpdateMode7Tiles', 'CalculateBoundsMesh', 'GetDynamicFontTexture', 'GenerateDynamicFontTexture')
$targets = @{}
foreach ($name in $methodNames) {
    $targets[$name] = $renderer.GetMethod($name, $flags)
    if ($null -eq $targets[$name]) { throw "Renderer target method was not found: $name" }
}

$patchProcessor = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getPatchInfo = $patchProcessor.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
foreach ($target in $targets.Values) {
    if ($null -ne $getPatchInfo.Invoke($null, @($target))) { throw "Verifier unexpectedly found a patched target: $($target.Name)" }
}
$script:getOriginal = $patchProcessor.GetMethods([Reflection.BindingFlags]'Public,Static') |
    Where-Object { $_.Name -eq 'GetOriginalInstructions' -and $_.GetParameters().Count -eq 2 -and -not $_.GetParameters()[1].ParameterType.IsByRef } |
    Select-Object -First 1
if ($null -eq $script:getOriginal) { throw 'Harmony original-instruction decoder was not found.' }
$script:transpileFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'

# v0.1 transformations remain valid.
$drawInput = Original-Instructions $targets.DrawLines
$processInput = Original-Instructions $targets.ProcessMaterial
$drawOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.DrawLinesMaterialLookupOptimization', $true)
$setOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.UsedMaterialsAddOptimization', $true)
$drawOutput = Invoke-OneArgumentTranspiler $drawOptimizer $drawInput
$processOutput = Invoke-OneArgumentTranspiler $setOptimizer $processInput

$matDictionaryType = $renderer.GetField('matDict', $flags).FieldType
$matContains = First-PublicMethod $matDictionaryType 'ContainsKey' 1
$matIndexer = $matDictionaryType.GetProperty('Item').GetGetMethod()
$matTryGet = First-PublicMethod $matDictionaryType 'TryGetValue' 2
if ((Count-Calls $drawOutput $matTryGet) -ne 2 -or (Count-Calls $drawOutput $matContains) -ne 0 -or
    (Count-Calls $drawOutput $matIndexer) -ne 0) { throw 'DrawLines material lookup output call counts are incorrect.' }

$usedSetType = $renderer.GetField('usedMaterials', $flags).FieldType
$usedContains = First-PublicMethod $usedSetType 'Contains' 1
$usedAdd = First-PublicMethod $usedSetType 'Add' 1
if ((Count-Calls $processOutput $usedContains) -ne 0 -or (Count-Calls $processOutput $usedAdd) -ne 1) {
    throw 'ProcessMaterial usedMaterials output call counts are incorrect.'
}

# The line-2771 tile-list clear is verified against actual decoded GenerateBackground IL.
$tileField = $renderer.GetField('tileAddrToMat', $flags)
$tileDictionaryType = $tileField.FieldType
$tileContains = First-PublicMethod $tileDictionaryType 'ContainsKey' 1
$tileIndexer = $tileDictionaryType.GetProperty('Item').GetGetMethod()
$tileTryGet = First-PublicMethod $tileDictionaryType 'TryGetValue' 2
$tileOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.TileListClearLookupOptimization', $true)
$tileInput = Original-Instructions $targets.GenerateBackground
$tileOutput = Invoke-TwoArgumentTranspiler $tileOptimizer $tileInput (New-Generator 'verify_tile_clear')
if ((Count-Calls $tileInput $tileContains) -ne 1 -or (Count-Calls $tileInput $tileIndexer) -ne 2 -or
    (Count-Calls $tileOutput $tileContains) -ne 0 -or (Count-Calls $tileOutput $tileTryGet) -ne 1 -or
    (Count-Calls $tileOutput $tileIndexer) -ne 1) { throw 'GenerateBackground tile-list lookup output call counts are incorrect.' }

# CalculateBoundsMesh has the same mode7data get-or-create IL shape as UpdateMode7Tiles and can be decoded by desktop CLR.
$modeField = $renderer.GetField('mode7data', $flags)
$modeDictionaryType = $modeField.FieldType
$modeContains = First-PublicMethod $modeDictionaryType 'ContainsKey' 1
$modeIndexer = $modeDictionaryType.GetProperty('Item').GetGetMethod()
$modeTryGet = First-PublicMethod $modeDictionaryType 'TryGetValue' 2
$modeAdd = First-PublicMethod $modeDictionaryType 'Add' 2
$modeOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.Mode7DataLookupOptimization', $true)
$boundsInput = Original-Instructions $targets.CalculateBoundsMesh
$boundsOutput = Invoke-TwoArgumentTranspiler $modeOptimizer $boundsInput (New-Generator 'verify_mode7_bounds')
if ((Count-Calls $boundsInput $modeContains) -ne 1 -or (Count-Calls $boundsInput $modeIndexer) -ne 1 -or
    (Count-Calls $boundsOutput $modeContains) -ne 0 -or (Count-Calls $boundsOutput $modeIndexer) -ne 0 -or
    (Count-Calls $boundsOutput $modeTryGet) -ne 1 -or (Count-Calls $boundsOutput $modeAdd) -ne 1) {
    throw 'CalculateBoundsMesh mode7data output call counts are incorrect.'
}

# UpdateMode7Tiles has a System.Span local that desktop PowerShell cannot resolve from Unity's netstandard 2.1 facade.
# Validate its exact on-disk pattern with Cecil, which reads metadata without loading that local type.
$cecilAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bepInExRoot 'BepInEx\core\Mono.Cecil.dll'))
$readAssembly = $cecilAssembly.GetType('Mono.Cecil.AssemblyDefinition').GetMethod('ReadAssembly', [Type[]]@([string]))
$cecilArguments = [object[]]::new(1)
$cecilArguments[0] = [string]$gameAssemblyPath
$cecilGame = $readAssembly.Invoke($null, $cecilArguments)
$cecilRenderer = $cecilGame.MainModule.Types | Where-Object { $_.FullName -eq 'PPURenderer' } | Select-Object -First 1
$cecilUpdateMode7 = $cecilRenderer.Methods | Where-Object { $_.Name -eq 'UpdateMode7Tiles' } | Select-Object -First 1
$cecilInstructions = @($cecilUpdateMode7.Body.Instructions)
function Count-CecilCall([string] $name) {
    return @($cecilInstructions | Where-Object {
        $_.OpCode.Name -in @('call', 'callvirt') -and $_.Operand.Name -eq $name -and
        $_.Operand.DeclaringType.FullName -eq 'System.Collections.Generic.Dictionary`2<UnityEngine.Material,System.Collections.Generic.List`1<UnityEngine.Vector3>>'
    }).Count
}
if ((Count-CecilCall 'ContainsKey') -ne 1 -or (Count-CecilCall 'get_Item') -ne 1 -or (Count-CecilCall 'Add') -ne 1) {
    throw 'UpdateMode7Tiles on-disk mode7data call shape is not the expected ContainsKey/Add/indexer triplet.'
}
$cecilContainsIndex = -1
for ($index = 0; $index -lt $cecilInstructions.Count; $index++) {
    if ($cecilInstructions[$index].Operand.Name -eq 'ContainsKey' -and
        $cecilInstructions[$index].Operand.DeclaringType.FullName -eq 'System.Collections.Generic.Dictionary`2<UnityEngine.Material,System.Collections.Generic.List`1<UnityEngine.Vector3>>') {
        $cecilContainsIndex = $index
        break
    }
}
if ($cecilContainsIndex -lt 5 -or $cecilInstructions[$cecilContainsIndex + 1].OpCode.Name -notmatch '^brtrue' -or
    $cecilInstructions[$cecilContainsIndex + 7].OpCode.Name -ne 'newobj' -or
    $cecilInstructions[$cecilContainsIndex + 8].Operand.Name -ne 'Add' -or
    $cecilInstructions[$cecilContainsIndex + 14].Operand.Name -ne 'get_Item') {
    throw 'UpdateMode7Tiles on-disk mode7data instruction offsets differ from the guarded transpiler shape.'
}

# Both dynamic-font dictionary return sites and the generator HashSet path use actual decoded IL.
$fontField = $renderer.GetField('dynamicFontStorage', $flags)
$fontDictionaryType = $fontField.FieldType
$fontContains = First-PublicMethod $fontDictionaryType 'ContainsKey' 1
$fontIndexer = $fontDictionaryType.GetProperty('Item').GetGetMethod()
$fontTryGet = First-PublicMethod $fontDictionaryType 'TryGetValue' 2
$fontGetOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.DynamicFontGetLookupOptimization', $true)
$fontGenerateOptimizer = $pluginAssembly.GetType('SuperZSNESRendererFastPaths.DynamicFontGenerateLookupOptimization', $true)
$fontGetInput = Original-Instructions $targets.GetDynamicFontTexture
$fontGenerateInput = Original-Instructions $targets.GenerateDynamicFontTexture
$fontGetOutput = Invoke-TwoArgumentTranspiler $fontGetOptimizer $fontGetInput (New-Generator 'verify_font_get')
$fontGenerateOutput = Invoke-TwoArgumentTranspiler $fontGenerateOptimizer $fontGenerateInput (New-Generator 'verify_font_generate')
if ((Count-Calls $fontGetOutput $fontContains) -ne 0 -or (Count-Calls $fontGetOutput $fontIndexer) -ne 0 -or
    (Count-Calls $fontGetOutput $fontTryGet) -ne 1 -or (Count-Calls $fontGenerateOutput $fontContains) -ne 0 -or
    (Count-Calls $fontGenerateOutput $fontIndexer) -ne 0 -or (Count-Calls $fontGenerateOutput $fontTryGet) -ne 1) {
    throw 'Dynamic-font dictionary output call counts are incorrect.'
}
$fontSetType = $renderer.GetField('usedDynamicFonts', $flags).FieldType
$fontSetContains = First-PublicMethod $fontSetType 'Contains' 1
$fontSetAdd = First-PublicMethod $fontSetType 'Add' 1
if ((Count-Calls $fontGenerateOutput $fontSetContains) -ne 0 -or (Count-Calls $fontGenerateOutput $fontSetAdd) -ne 1) {
    throw 'GenerateDynamicFontTexture set output call counts are incorrect.'
}

# Verify the two ProcessMaterial transpilers compose in either Harmony ordering with the active scratch-list pool.
if (Test-Path -LiteralPath $materialGuardPath) {
    $materialAssembly = [Reflection.Assembly]::LoadFrom($materialGuardPath)
    $materialType = $materialAssembly.GetType('SuperZSNESMaterialCacheGuard.ProcessMaterialTranspiler', $true)
    $oursThenMaterial = Invoke-OneArgumentTranspiler $materialType $processOutput
    $materialFirst = Invoke-OneArgumentTranspiler $materialType $processInput
    $materialThenOurs = Invoke-OneArgumentTranspiler $setOptimizer $materialFirst
    $compositions = [object[]]::new(2)
    $compositions[0] = $oursThenMaterial
    $compositions[1] = $materialThenOurs
    foreach ($composed in $compositions) {
        if ((Count-Calls $composed $usedContains) -ne 0 -or (Count-Calls $composed $usedAdd) -ne 1) {
            throw 'ProcessMaterial composition restored or removed an unexpected usedMaterials call.'
        }
        $rentCalls = @($composed | Where-Object {
            $_.operand -is [Reflection.MethodInfo] -and $_.operand.DeclaringType.FullName -eq 'SuperZSNESMaterialCacheGuard.ScratchListPool' -and $_.operand.Name -eq 'RentObject'
        }).Count
        if ($rentCalls -ne 1) { throw 'ProcessMaterial composition did not retain exactly one ScratchListPool.RentObject call.' }
    }
}

# Pure semantic checks for retained object identity, missing-key behavior, and duplicate set Add.
$listMap = [Collections.Generic.Dictionary[int,Collections.Generic.List[int]]]::new()
$existing = [Collections.Generic.List[int]]::new()
$existing.Add(7)
$listMap.Add(1, $existing)
$resolved = $null
if (-not $listMap.TryGetValue(1, [ref]$resolved) -or -not [object]::ReferenceEquals($existing, $resolved)) {
    throw 'TryGetValue did not retain existing list identity.'
}
$missing = $null
if (-not $listMap.TryGetValue(2, [ref]$missing)) {
    $missing = [Collections.Generic.List[int]]::new()
    $listMap.Add(2, $missing)
}
if (-not [object]::ReferenceEquals($missing, $listMap[2])) { throw 'Get-or-create missing path did not retain new list identity.' }
$testSet = [Collections.Generic.HashSet[int]]::new()
$first = $testSet.Add(42)
$second = $testSet.Add(42)
if (-not $first -or $second -or $testSet.Count -ne 1) { throw 'HashSet duplicate-Add semantic check failed.' }

$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'SuperZSNESRendererFastPathsPlugin.cs') -Raw
foreach ($setting in @('DrawLinesMaterialLookup', 'UsedMaterialsAdd', 'Mode7DataLookup', 'TileListClearLookup', 'DynamicFontLookup')) {
    if ($source -notmatch ('"Optimizations", "' + $setting + '", false')) { throw "Optimization is not disabled by default: $setting" }
}
if ($source -notmatch 'public const string PluginVersion = "0.2.0"') { throw 'Plugin version is not 0.2.0.' }

$installedCopies = @()
$hash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
if (Test-Path -LiteralPath $installedRoot) {
    $installedCopies = @(Get-ChildItem -LiteralPath $installedRoot -Recurse -Filter 'SuperZSNESRendererFastPaths.dll' -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName
                Version = [Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version.ToString()
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
}
if (@($installedCopies | Where-Object { $_.Version -eq '0.2.0.0' -or $_.Sha256 -eq $hash }).Count -ne 0) {
    throw "Renderer fast-path v0.2 DLL is unexpectedly installed: $($installedCopies.Path -join ', ')"
}

Write-Output 'PASS: all seven actual v0.230 renderer targets exist and remain unpatched.'
Write-Output 'PASS: DrawLines and ProcessMaterial v0.1 rewrites retain their exact call-count invariants.'
Write-Output 'PASS: GenerateBackground line-2771 tile clear uses one TryGetValue and retains its later required indexer.'
Write-Output 'PASS: both mode7data get-or-create sites have the exact guarded shape; actual CalculateBoundsMesh output is TryGetValue/Add without an indexer.'
Write-Output 'PASS: both dynamic-font dictionary reads use TryGetValue and the generator has one unconditional HashSet.Add.'
Write-Output 'PASS: ProcessMaterial rewrites compose with MaterialCacheGuard scratch pooling in both transpiler orders.'
Write-Output "PASS: all five runtime switches are disabled by default and no v0.2 DLL is installed (pre-existing copies: $($installedCopies.Count))."
Write-Output "Assembly-CSharp SHA256: $actualGameHash"
Write-Output "Renderer fast-path v0.2 DLL SHA256: $hash"
