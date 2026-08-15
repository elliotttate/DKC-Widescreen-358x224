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
$expectedWidescreenSha256 = '2F7086758DC9CB744104DB422C40C4A1CA3B9988945097293F4FE4633E6F5A70'
$expectedMsu1DeluxeSha256 = '6BBC08977C9E7E2296CF7E3FCE2677A985A8D8E70EC8C90205ECE7E58C001296'
$expectedMsu1RestorationSha256 = 'E916722ED54C0CB5A088099FCD323FDB480CFFF9DBAB5D9F479BA4747660D647'
$expected16x9Sha256 = '613C804C761ACD8BF1213BEF867D15B637688B8EB969FC96B878CE94223AA4E4'
$expected16x9Msu1DeluxeSha256 = 'C558A1634E048C4A8698128D21EC2950F5AD2B29926A796DB66D64C4A5F3A874'
$expected16x9Msu1RestorationSha256 = '40DFC77F80E7248AA62A5519B679582EE92BDB7EF9355438C614FC9F6A428921'

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
