param(
    [Parameter(Mandatory=$true)][string]$DisassemblyRoot,
    [Parameter(Mandatory=$true)][string]$RomPath,
    [string]$OutputPath,
    [switch]$EnableMsu1Deluxe,
    [switch]$EnableMsu1Restoration,
    [switch]$SkipExtraction
)

$ErrorActionPreference = 'Stop'
$expectedUpstream = 'c2080f40469c716923f550706509a0d354229841'
$expectedRomMd5 = '30C5F292FF4CBBFCC00FD8FA96C2DE3B'
$expectedWidescreenSha256 = 'B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D'
$expectedMsu1DeluxeSha256 = 'FD2950B3AAE287E24F8D8B665AFBC3BE0EC3EEC07AA19DE055427DF76BD46AF5'
$expectedMsu1RestorationSha256 = '4484CB5374F3C04E9F8DA1880C21D85D0C0403286CFABB65639BAD7CFC55A5A5'

if ($EnableMsu1Deluxe -and $EnableMsu1Restoration) {
    throw 'Choose only one MSU-1 music mode.'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $artifactName = if ($EnableMsu1Deluxe) {
        'DKC_Widescreen_358x224_MSU1_Deluxe.sfc'
    } elseif ($EnableMsu1Restoration) {
        'DKC_Widescreen_358x224_MSU1_Restoration.sfc'
    } else {
        'DKC_Widescreen_358x224.sfc'
    }
    $OutputPath = Join-Path $PSScriptRoot "..\artifacts\$artifactName"
}

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
$romId = if ($EnableMsu1Deluxe) {
    'HACK_DKC_Widescreen_358x224_MSU1Deluxe'
} elseif ($EnableMsu1Restoration) {
    'HACK_DKC_Widescreen_358x224_MSU1Restoration'
} else {
    'HACK_DKC_Widescreen_358x224'
}

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
    if ($EnableMsu1Deluxe -or $EnableMsu1Restoration) {
        & $asar --fix-checksum=on --define GameID='DKC1' --define ROMID=$romId --define FileType=2 $assemble $working
    } else {
        & $asar --define GameID='DKC1' --define ROMID=$romId --define FileType=2 $assemble $working
    }
    if ($LASTEXITCODE) { throw 'Widescreen patch/finalization failed.' }
} finally { Pop-Location }

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
Copy-Item -LiteralPath $working -Destination $OutputPath -Force
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputPath).Hash
$expectedOutputSha256 = if ($EnableMsu1Deluxe) {
    $expectedMsu1DeluxeSha256
} elseif ($EnableMsu1Restoration) {
    $expectedMsu1RestorationSha256
} else {
    $expectedWidescreenSha256
}
if ($expectedOutputSha256 -and $sha -ne $expectedOutputSha256) {
    throw "Build completed but hash differs. Expected $expectedOutputSha256, got $sha."
}
if ($expectedOutputSha256) {
    Write-Host "Built and verified: $OutputPath" -ForegroundColor Green
} else {
    Write-Host "Built (hash not yet locked): $OutputPath ($sha)" -ForegroundColor Yellow
}
