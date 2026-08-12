param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [switch]$Enable
)

$ErrorActionPreference = 'Stop'
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$expected = '0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A'
$gameAssembly = Join-Path $GameRoot 'GameAssembly.dll'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash -ne $expected) {
    throw 'GameRoot is not the verified SuperZSNES v0.300 x86 IL2CPP build.'
}
$running = @(Get-Process SUPERZSNES -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$GameRoot*" })
if ($running.Count) { throw "Close SuperZSNES before installing: $($running.Id -join ', ')" }
$source = Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESNativeAtlasDirtyFixIL2CPP.dll'
if (-not (Test-Path -LiteralPath $source)) { throw 'Build the Release plugin first.' }
$directory = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESNativeAtlasDirtyFixIL2CPP'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $directory (Split-Path $source -Leaf)) -Force
if ($Enable) {
    $config = Join-Path $GameRoot 'BepInEx\config\dev.local.superzsnes.nativeatlasdirtyfix.il2cpp.cfg'
    @'
[Patch]

Enabled = true
'@ | Set-Content -LiteralPath $config -Encoding UTF8
}
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $directory (Split-Path $source -Leaf))
