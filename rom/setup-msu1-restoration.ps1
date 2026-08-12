param(
    [Parameter(Mandatory=$true)][string]$RomPath,
    [Parameter(Mandatory=$true)][string]$AudioPackPath,
    [string]$DestinationDirectory,
    [switch]$UseAlternateTrack10
)

$ErrorActionPreference = 'Stop'
$RomPath = (Resolve-Path -LiteralPath $RomPath).Path
$AudioPackPath = (Resolve-Path -LiteralPath $AudioPackPath).Path
if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Split-Path $RomPath -Parent
}
$DestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

$romBaseName = [IO.Path]::GetFileNameWithoutExtension($RomPath)
$destinationRom = Join-Path $DestinationDirectory ($romBaseName + '.sfc')
if (-not [IO.Path]::GetFullPath($RomPath).Equals([IO.Path]::GetFullPath($destinationRom), [StringComparison]::OrdinalIgnoreCase)) {
    if (Test-Path -LiteralPath $destinationRom) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $RomPath).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationRom).Hash) {
            throw "Destination ROM already exists with different contents: $destinationRom"
        }
    } else {
        Copy-Item -LiteralPath $RomPath -Destination $destinationRom
    }
}

$validated = @{}
for ($track = 1; $track -le 27; $track++) {
    $filename = if ($track -eq 10 -and $UseAlternateTrack10) { 'dkc_msu-10_alt.pcm' } else { "dkc_msu-$track.pcm" }
    $source = Join-Path $AudioPackPath $filename
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing restoration MSU-1 track $track`: $source"
    }
    $file = Get-Item -LiteralPath $source
    if ($file.Length -lt 12 -or (($file.Length - 8) % 4) -ne 0) {
        throw "Track $track has an invalid MSU1-PCM size: $source"
    }
    $stream = [IO.File]::OpenRead($source)
    try {
        $header = [byte[]]::new(8)
        if ($stream.Read($header, 0, 8) -ne 8 -or [Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'MSU1') {
            throw "Track $track has an invalid MSU1 header: $source"
        }
        $loopSample = [BitConverter]::ToUInt32($header, 4)
        $sampleFrames = [uint64](($file.Length - 8) / 4)
        if ([uint64]$loopSample -ge $sampleFrames) {
            throw "Track $track has an out-of-range loop point: $source"
        }
    } finally {
        $stream.Dispose()
    }
    $validated[$track] = $source
}

$msuPath = Join-Path $DestinationDirectory ($romBaseName + '.msu')
if (-not (Test-Path -LiteralPath $msuPath)) {
    New-Item -ItemType File -Path $msuPath | Out-Null
}

$created = 0
for ($track = 1; $track -le 27; $track++) {
    $source = $validated[$track]
    $destination = Join-Path $DestinationDirectory ("$romBaseName-$track.pcm")
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $source).Length -ne (Get-Item -LiteralPath $destination).Length) {
            throw "Existing destination track has a different size: $destination"
        }
        continue
    }
    New-Item -ItemType HardLink -Path $destination -Target $source | Out-Null
    $created++
}

$romHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationRom).Hash
Write-Host 'Prepared DKC Restoration MSU-1 runtime bundle:' -ForegroundColor Green
Write-Host "  ROM:      $destinationRom"
Write-Host "  SHA256:   $romHash"
Write-Host "  Marker:   $msuPath"
Write-Host "  Tracks:   27 valid ($created new hard links)"
Write-Host "  Track 10: $(if ($UseAlternateTrack10) { 'alternate version' } else { 'main version' })"
