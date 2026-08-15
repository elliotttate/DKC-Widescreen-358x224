param(
    [Parameter(Mandatory=$true)][string]$CleanRomPath,
    [Parameter(Mandatory=$true)][string]$StandardRomPath,
    [Parameter(Mandatory=$true)][string]$DeluxeRomPath,
    [Parameter(Mandatory=$true)][string]$RestorationRomPath,
    [Parameter(Mandatory=$true)][string]$Standard16x9RomPath,
    [Parameter(Mandatory=$true)][string]$Deluxe16x9RomPath,
    [Parameter(Mandatory=$true)][string]$Restoration16x9RomPath,
    [string]$SuperZSNESRoot,
    [string]$Version = 'v1.1.2',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo "artifacts\releases\$Version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$patchDirectory = Join-Path $OutputDirectory 'patches'
$verifyDirectory = Join-Path $OutputDirectory 'verification'
$packageDirectory = Join-Path $OutputDirectory 'package'

$expected = @{
    Clean = @{ Path=$CleanRomPath; Algorithm='MD5'; Hash='30C5F292FF4CBBFCC00FD8FA96C2DE3B' }
    Standard = @{ Path=$StandardRomPath; Algorithm='SHA256'; Hash='03EA182F7D0AA147BD020CB7B00F98E785D8BB00AAA1DBA95F458C33FDBBF34B' }
    Deluxe = @{ Path=$DeluxeRomPath; Algorithm='SHA256'; Hash='F213800099DC4C35D7B69A249FC4A8A98FE9FE8D65FC8724096E6C2C6B568C0E' }
    Restoration = @{ Path=$RestorationRomPath; Algorithm='SHA256'; Hash='CD6DA8C7C981118785014ABF1823BB3877389360587462D2CC247DE3EA2A7A79' }
    Standard16x9 = @{ Path=$Standard16x9RomPath; Algorithm='SHA256'; Hash='F6BDF57A563C290E66A7726190DC22C754D4D42DBB4DF62C77C8CE6C05E7D144' }
    Deluxe16x9 = @{ Path=$Deluxe16x9RomPath; Algorithm='SHA256'; Hash='03A7B36933C11E30561B65FFBA01EC02FC18A124979019FC30154148668DF64B' }
    Restoration16x9 = @{ Path=$Restoration16x9RomPath; Algorithm='SHA256'; Hash='D8991560242D3BE1615D86890263E323F47A7560201D93893BD8C8EC53268F05' }
}
foreach ($entry in $expected.GetEnumerator()) {
    $path = (Resolve-Path -LiteralPath $entry.Value.Path).Path
    $entry.Value.Path = $path
    $actual = (Get-FileHash -Algorithm $entry.Value.Algorithm -LiteralPath $path).Hash
    if ($actual -ne $entry.Value.Hash) {
        throw "$($entry.Key) input hash mismatch. Expected $($entry.Value.Hash), got $actual."
    }
}

# These are disposable staging directories beneath the explicitly selected
# release output. Clearing them makes repeated builds byte-for-byte structural
# equivalents and prevents stale nested package content.
foreach ($stagingDirectory in @($patchDirectory,$verifyDirectory,$packageDirectory)) {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $patchDirectory,$verifyDirectory,$packageDirectory -Force | Out-Null
$project = Join-Path $repo 'tools\DKCWidescreenPatcher\DKCWidescreenPatcher.csproj'
& dotnet build $project -c Release
if ($LASTEXITCODE) { throw 'Patcher bootstrap build failed.' }
$patcher = Join-Path $repo 'tools\DKCWidescreenPatcher\bin\Release\net48\DKC-Widescreen-Patcher.exe'

function Quote-ProcessArgument([string]$value) {
    return '"' + $value.Replace('"', '\"') + '"'
}

function Invoke-Patcher([string[]]$arguments) {
    $quoted = @($arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
    $process = Start-Process -FilePath $patcher -ArgumentList $quoted -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        $errorFile = Join-Path (Split-Path $patcher -Parent) 'patcher-error.txt'
        $detail = if (Test-Path $errorFile) { Get-Content $errorFile -Raw } else { 'No error log was written.' }
        throw "Patcher command failed with exit code $($process.ExitCode).`n$detail"
    }
}

$patches = @(
    @{ Id='standard'; Target=$expected.Standard.Path; Metadata="DKC Widescreen 358x224 $Version" },
    @{ Id='msu1-deluxe'; Target=$expected.Deluxe.Path; Metadata="DKC Widescreen 358x224 + Deluxe MSU-1 $Version" },
    @{ Id='msu1-restoration'; Target=$expected.Restoration.Path; Metadata="DKC Widescreen 358x224 + Restoration MSU-1 $Version" },
    @{ Id='16x9-standard'; Target=$expected.Standard16x9.Path; Metadata="DKC Widescreen 398x224 $Version" },
    @{ Id='16x9-msu1-deluxe'; Target=$expected.Deluxe16x9.Path; Metadata="DKC Widescreen 398x224 + Deluxe MSU-1 $Version" },
    @{ Id='16x9-msu1-restoration'; Target=$expected.Restoration16x9.Path; Metadata="DKC Widescreen 398x224 + Restoration MSU-1 $Version" }
)
foreach ($patch in $patches) {
    $destination = Join-Path $patchDirectory ($patch.Id + '.bps')
    Invoke-Patcher @('--create-bps',$expected.Clean.Path,$patch.Target,$destination,$patch.Metadata)
}

# Rebuild with the verified BPS files embedded. The adjacent copies remain in
# the ZIP for interoperability with standard patchers.
& dotnet build $project -c Release -p:PatchResourceDirectory=$patchDirectory
if ($LASTEXITCODE) { throw 'Embedded patcher build failed.' }
Invoke-Patcher @('--verify-embedded',$expected.Clean.Path,$verifyDirectory)

$verifiedOutputs = @{
    'DKC_Widescreen_358x224.sfc' = $expected.Standard.Hash
    'DKC_Widescreen_358x224_MSU1_Deluxe.sfc' = $expected.Deluxe.Hash
    'DKC_Widescreen_358x224_MSU1_Restoration.sfc' = $expected.Restoration.Hash
    'DKC_Widescreen_398x224.sfc' = $expected.Standard16x9.Hash
    'DKC_Widescreen_398x224_MSU1_Deluxe.sfc' = $expected.Deluxe16x9.Hash
    'DKC_Widescreen_398x224_MSU1_Restoration.sfc' = $expected.Restoration16x9.Hash
}
foreach ($name in $verifiedOutputs.Keys) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $verifyDirectory $name)).Hash
    if ($actual -ne $verifiedOutputs[$name]) { throw "Embedded patch verification failed for $name." }
}

$il2cppProject = Join-Path $repo 'mods\SuperZSNESDKCFramebufferRendererIL2CPP\SuperZSNESDKCFramebufferRendererIL2CPP.csproj'
$dependencyRoot = if (-not [string]::IsNullOrWhiteSpace($SuperZSNESRoot)) {
    (Resolve-Path -LiteralPath $SuperZSNESRoot).Path
} else {
    Join-Path $repo '.deps\SuperZSNES_v0.300'
}
if (-not (Test-Path -LiteralPath (Join-Path $dependencyRoot 'BepInEx\interop\Assembly-CSharp.dll'))) {
    throw 'Supply -SuperZSNESRoot pointing to a BepInEx 6 IL2CPP-enabled SuperZSNES v0.300 installation.'
}
& dotnet build $il2cppProject -c Release -p:BepInExIl2CppRoot=$dependencyRoot
if ($LASTEXITCODE) { throw 'IL2CPP renderer build failed.' }
$renderer = Join-Path $repo 'mods\SuperZSNESDKCFramebufferRendererIL2CPP\bin\Release\net6.0\SuperZSNESDKCFramebufferRendererIL2CPP.dll'
if (-not (Test-Path $renderer)) { throw "Renderer output missing: $renderer" }

Copy-Item -LiteralPath $patcher -Destination (Join-Path $packageDirectory 'DKC-Widescreen-Patcher.exe') -Force
Copy-Item -LiteralPath $renderer -Destination (Join-Path $packageDirectory 'SuperZSNESDKCFramebufferRendererIL2CPP.dll') -Force
Copy-Item -LiteralPath (Join-Path $repo 'packaging\README.txt') -Destination (Join-Path $packageDirectory 'README.txt') -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repo 'NOTICE.md') -Destination (Join-Path $packageDirectory 'NOTICE.md') -Force
Copy-Item -LiteralPath $patchDirectory -Destination (Join-Path $packageDirectory 'patches') -Recurse -Force

$sums = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($packageDirectory.Length).TrimStart('\')
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(),$relative
}
$sums | Set-Content -LiteralPath (Join-Path $packageDirectory 'SHA256SUMS.txt') -Encoding ASCII

$zip = Join-Path $OutputDirectory "DKC-Widescreen-$Version-Windows.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zip -CompressionLevel Optimal

$releaseNotes = @"
# DKC Widescreen $Version

This bugfix release provides a one-click Windows ROM patcher, standard BPS
patches, and the SuperZSNES v0.300 IL2CPP framebuffer renderer.

## Included

- Standard 358x224 widescreen patch with original SNES music
- Optional 60-track Deluxe MSU-1 compatibility patch
- Optional 27-track Restoration MSU-1 compatibility patch
- Optional 398x224 near-exact 16:9 profile in all three music modes
- SuperZSNES v0.300 IL2CPP framebuffer renderer v0.1.10 with automatic profile detection
- Native-width black sidebars for the Nintendo Presents and title splash scenes
- Correct type-9 section-controller reach for Slipslide Ride ropes and enemies
- Exact source and output checksum verification

No ROM, game assets, music, emulator, or BepInEx runtime is included. Supply a
legal, headerless DKC USA v1.0 ROM with MD5
`30c5f292ff4cbbfcc00fd8fa96c2de3b`.

Download the Windows ZIP, extract it, and run `DKC-Widescreen-Patcher.exe`.
"@
$releaseNotes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-notes.md') -Encoding UTF8

Write-Host "Release package built and verified: $zip" -ForegroundColor Green
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
