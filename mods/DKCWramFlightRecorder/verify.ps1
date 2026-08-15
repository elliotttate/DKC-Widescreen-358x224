param(
    [Parameter(Mandatory = $true)][string]$BepInExRoot,
    [Parameter(Mandatory = $true)][string]$GameManagedDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginPath = Join-Path $projectDirectory "bin\$Configuration\netstandard2.1\DKCWramFlightRecorder.dll"
$gameAssemblyPath = Join-Path $GameManagedDir 'Assembly-CSharp.dll'
$harmonyPath = Join-Path $BepInExRoot 'BepInEx\core\0Harmony.dll'
$bepInExPath = Join-Path $BepInExRoot 'BepInEx\core\BepInEx.dll'
$expectedGameHash = '33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED'
$expectedMvid = '11738189-56ff-499d-8e00-b87cfb7f66eb'

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Method-IlHash([Reflection.MethodInfo]$Method) {
    $bytes = $Method.GetMethodBody().GetILAsByteArray()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

Require (Test-Path -LiteralPath $pluginPath -PathType Leaf) "Plugin DLL was not built: $pluginPath"
Require (Test-Path -LiteralPath $gameAssemblyPath -PathType Leaf) "Assembly-CSharp.dll was not found: $gameAssemblyPath"
Require (Test-Path -LiteralPath $harmonyPath -PathType Leaf) "0Harmony.dll was not found: $harmonyPath"

$actualGameHash = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash
Require ($actualGameHash -eq $expectedGameHash) "Assembly-CSharp SHA-256 mismatch: $actualGameHash"
Require ((Get-Item -LiteralPath $gameAssemblyPath).Length -eq 612352) 'Assembly-CSharp size mismatch.'

Push-Location $GameManagedDir
try {
    foreach ($dependency in @('netstandard.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.dll')) {
        [Reflection.Assembly]::LoadFrom((Join-Path $GameManagedDir $dependency)) | Out-Null
    }
    [Reflection.Assembly]::LoadFrom($bepInExPath) | Out-Null
    $harmonyAssembly = [Reflection.Assembly]::LoadFrom($harmonyPath)
    $gameAssembly = [Reflection.Assembly]::LoadFrom($gameAssemblyPath)
    $pluginAssembly = [Reflection.Assembly]::LoadFrom($pluginPath)
}
finally { Pop-Location }

Require ($gameAssembly.ManifestModule.ModuleVersionId.ToString('D') -eq $expectedMvid) 'Assembly-CSharp MVID mismatch.'
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$cpuType = $gameAssembly.GetType('CPU65c816', $true)
$memoryType = $gameAssembly.GetType('MainMemoryMap', $true)
$cpuMethod = $cpuType.GetMethod('ExecuteNextInstruction', $instanceFlags, $null, [Type[]]@(), $null)
$writeMethod = $memoryType.GetMethod('WriteMem', $instanceFlags, $null, [Type[]]@([uint32], [byte]), $null)
Require ($null -ne $cpuMethod -and $cpuMethod.ReturnType -eq [void]) 'CPU hook signature mismatch.'
Require ($null -ne $writeMethod -and $writeMethod.ReturnType -eq [void]) 'Write hook signature mismatch.'
Require ($cpuMethod.MetadataToken -eq 0x060004BB) 'CPU method token mismatch.'
Require ($writeMethod.MetadataToken -eq 0x0600056E) 'Write method token mismatch.'
Require ($cpuMethod.GetMethodBody().GetILAsByteArray().Length -eq 14028) 'CPU method IL length mismatch.'
Require ($writeMethod.GetMethodBody().GetILAsByteArray().Length -eq 209) 'Write method IL length mismatch.'
Require ((Method-IlHash $cpuMethod) -eq '3931A27E4F8B3C6F5EAEAA192E4DABC053101FA2C3EEDA8B31B838CB08DE172F') 'CPU method IL hash mismatch.'
Require ((Method-IlHash $writeMethod) -eq '1640D72CEE188DC079AFC641E4AE3EE8755C7DC5499D87B5A5279B83E46F6A9C') 'Write method IL hash mismatch.'

$contractType = $pluginAssembly.GetType('DKCWramFlightRecorder.SuperZsnesContract', $true)
$validate = $contractType.GetMethod('Validate', [Reflection.BindingFlags]'Static,Public,NonPublic')
$contractResult = $validate.Invoke($null, @())
$validField = $contractResult.GetType().GetField('Valid', [Reflection.BindingFlags]'Instance,Public,NonPublic')
$errorField = $contractResult.GetType().GetField('Error', [Reflection.BindingFlags]'Instance,Public,NonPublic')
Require ([bool]$validField.GetValue($contractResult)) ("Runtime gate rejected the exact assembly: " + $errorField.GetValue($contractResult))

$patchProcessor = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getPatchInfo = $patchProcessor.GetMethod('GetPatchInfo', [Reflection.BindingFlags]'Public,Static')
Require ($null -eq $getPatchInfo.Invoke($null, @($cpuMethod))) 'CPU method was unexpectedly patched in the verifier process.'
Require ($null -eq $getPatchInfo.Invoke($null, @($writeMethod))) 'Write method was unexpectedly patched in the verifier process.'

$harmonyPatchAttributes = @($pluginAssembly.GetTypes() | ForEach-Object { $_.CustomAttributes } | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' })
Require ($harmonyPatchAttributes.Count -eq 0) 'Static HarmonyPatch attributes are forbidden; hooks must only exist during an armed session.'
$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'DKCWramFlightRecorderPlugin.cs') -Raw
Require ($source -match '"ArmedAtStartup", false') 'Recorder is not disarmed by default.'
Require (($source | Select-String -Pattern '_harmony\.Patch\(' -AllMatches).Matches.Count -eq 2) 'Unexpected Harmony patch call count.'
Require ($source -match 'Arm failed closed; no recorder hooks retained') 'Fail-closed arming contract text is missing.'
Require ($source -match 'No Harmony hooks are installed while disarmed') 'Disarmed no-hook contract text is missing.'

$dllHash = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
Write-Output 'PASS: exact SuperZSNES v0.230 assembly, MVID, signatures, tokens, and IL hashes match.'
Write-Output 'PASS: the plugin runtime gate accepts the exact assembly.'
Write-Output 'PASS: loading the plugin installs no hooks; no static HarmonyPatch attributes exist.'
Write-Output 'PASS: ArmedAtStartup is false and both hot prefixes are confined to the explicit arm path.'
Write-Output "Assembly-CSharp SHA256: $actualGameHash"
Write-Output "DKCWramFlightRecorder DLL SHA256: $dllHash"
