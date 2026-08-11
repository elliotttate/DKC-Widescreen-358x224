param(
    [Parameter(Mandatory=$true)][string]$DisassemblyRoot,
    [Parameter(Mandatory=$true)][string]$RomPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\DKC_Widescreen_358x224.sfc'),
    [switch]$SkipExtraction
)

$ErrorActionPreference = 'Stop'
$expectedUpstream = 'c2080f40469c716923f550706509a0d354229841'
$expectedRomMd5 = '30C5F292FF4CBBFCC00FD8FA96C2DE3B'
$expectedOutputSha256 = 'B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D'

$DisassemblyRoot = (Resolve-Path $DisassemblyRoot).Path
$RomPath = (Resolve-Path $RomPath).Path
$actualCommit = (& git -C $DisassemblyRoot rev-parse HEAD).Trim()
if ($actualCommit -ne $expectedUpstream) {
    throw "Expected upstream commit $expectedUpstream, got $actualCommit."
}

$romMd5 = (Get-FileHash -Algorithm MD5 -LiteralPath $RomPath).Hash
if ($romMd5 -ne $expectedRomMd5) {
    throw "Expected clean DKC USA v1.0 MD5 $expectedRomMd5, got $romMd5."
}

$overlayRoot = Join-Path $PSScriptRoot 'overlay'
Get-ChildItem -Recurse -File $overlayRoot | ForEach-Object {
    $relative = $_.FullName.Substring($overlayRoot.Length).TrimStart('\')
    $destination = Join-Path $DisassemblyRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

$gameRoot = Join-Path $DisassemblyRoot 'DKC1'
$scriptsRoot = Join-Path $gameRoot 'AsarScripts'
$extractorRom = Join-Path $scriptsRoot 'DKC1_USA1.sfc'
if (-not (Test-Path $extractorRom)) {
    try { New-Item -ItemType HardLink -Path $extractorRom -Target $RomPath | Out-Null }
    catch { Copy-Item -LiteralPath $RomPath -Destination $extractorRom }
}

if (-not $SkipExtraction) {
    Push-Location $scriptsRoot
    try {
        & cmd.exe /d /c 'ExtractAssets.bat DKC1_U1'
        if ($LASTEXITCODE -ne 0) { throw "Asset extraction failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
}

$asar = Join-Path $DisassemblyRoot 'Global\asar.exe'
$assemble = Join-Path $DisassemblyRoot 'Global\AssembleFile.asm'
$working = Join-Path $gameRoot 'DKC_Widescreen_358x224 (Hack).sfc'
$romId = 'HACK_DKC_Widescreen_358x224'

Push-Location $gameRoot
try {
    & $asar --fix-checksum=on --define GameID='DKC1' --define ROMID=$romId --define FileType=0 $assemble $working
    if ($LASTEXITCODE) { throw 'ROM initialization failed.' }
    & $asar --no-title-check --define GameID='DKC1' --define ROMID=$romId --define FileType=4 --define PathToFile='SPC700/InitializeSPC700.asm' $assemble 'SPC700\InitializeSPC700.bin'
    if ($LASTEXITCODE) { throw 'SPC700 initialization assembly failed.' }
    & $asar --no-title-check --define GameID='DKC1' --define ROMID=$romId --define FileType=4 --define PathToFile='SPC700/SPC700_Engine_DKC1.asm' $assemble 'SPC700\SPC700_Engine_DKC1.bin'
    if ($LASTEXITCODE) { throw 'SPC700 engine assembly failed.' }
    & $asar --define GameID='DKC1' --define ROMID=$romId --define FileType=1 $assemble $working
    if ($LASTEXITCODE) { throw 'Main ROM assembly failed.' }
    & $asar --define GameID='DKC1' --define ROMID=$romId --define FileType=2 $assemble $working
    if ($LASTEXITCODE) { throw 'Widescreen patch/finalization failed.' }
} finally { Pop-Location }

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
Copy-Item -LiteralPath $working -Destination $OutputPath -Force
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputPath).Hash
if ($sha -ne $expectedOutputSha256) {
    throw "Build completed but hash differs. Expected $expectedOutputSha256, got $sha."
}
Write-Host "Built and verified: $OutputPath" -ForegroundColor Green
