param(
    [Parameter(Mandatory=$true)][string]$DisassemblyRoot,
    [Parameter(Mandatory=$true)][string]$RomPath,
    [string]$OutputPath,
    [switch]$Aspect16x9,
    [switch]$EnableMsu1Deluxe,
    [switch]$EnableMsu1Restoration,
    [switch]$SkipExtraction
)

$ErrorActionPreference = 'Stop'
$expectedUpstream = 'c2080f40469c716923f550706509a0d354229841'
$expectedRomMd5 = '30C5F292FF4CBBFCC00FD8FA96C2DE3B'
$expectedWidescreenSha256 = '03EA182F7D0AA147BD020CB7B00F98E785D8BB00AAA1DBA95F458C33FDBBF34B'
$expectedMsu1DeluxeSha256 = 'F213800099DC4C35D7B69A249FC4A8A98FE9FE8D65FC8724096E6C2C6B568C0E'
$expectedMsu1RestorationSha256 = 'CD6DA8C7C981118785014ABF1823BB3877389360587462D2CC247DE3EA2A7A79'
$expected16x9Sha256 = 'F6BDF57A563C290E66A7726190DC22C754D4D42DBB4DF62C77C8CE6C05E7D144'
$expected16x9Msu1DeluxeSha256 = '03A7B36933C11E30561B65FFBA01EC02FC18A124979019FC30154148668DF64B'
$expected16x9Msu1RestorationSha256 = 'D8991560242D3BE1615D86890263E323F47A7560201D93893BD8C8EC53268F05'

if ($EnableMsu1Deluxe -and $EnableMsu1Restoration) {
    throw 'Choose only one MSU-1 music mode.'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $dimensions = if ($Aspect16x9) { '398x224' } else { '358x224' }
    $artifactName = if ($EnableMsu1Deluxe) {
        "DKC_Widescreen_${dimensions}_MSU1_Deluxe.sfc"
    } elseif ($EnableMsu1Restoration) {
        "DKC_Widescreen_${dimensions}_MSU1_Restoration.sfc"
    } else {
        "DKC_Widescreen_${dimensions}.sfc"
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
$dimensions = if ($Aspect16x9) { '398x224' } else { '358x224' }
$working = Join-Path $gameRoot "DKC_Widescreen_${dimensions} (Hack).sfc"
$romId = if ($Aspect16x9 -and $EnableMsu1Deluxe) {
    'HACK_DKC_Widescreen_398x224_MSU1Deluxe'
} elseif ($Aspect16x9 -and $EnableMsu1Restoration) {
    'HACK_DKC_Widescreen_398x224_MSU1Restoration'
} elseif ($Aspect16x9) {
    'HACK_DKC_Widescreen_398x224'
} elseif ($EnableMsu1Deluxe) {
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
$expectedOutputSha256 = if ($Aspect16x9 -and $EnableMsu1Deluxe) {
    $expected16x9Msu1DeluxeSha256
} elseif ($Aspect16x9 -and $EnableMsu1Restoration) {
    $expected16x9Msu1RestorationSha256
} elseif ($Aspect16x9) {
    $expected16x9Sha256
} elseif ($EnableMsu1Deluxe) {
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
