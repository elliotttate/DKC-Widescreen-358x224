param(
    [Parameter(Mandatory=$true)][string]$RomPath,
    [Parameter(Mandatory=$true)][string]$AudioPackPath,
    [string]$DestinationDirectory,
    [switch]$UseOptionalGangPlankGalleon
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
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $RomPath).Hash
        $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationRom).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Destination ROM already exists with different contents: $destinationRom"
        }
    } else {
        Copy-Item -LiteralPath $RomPath -Destination $destinationRom
    }
}

$validated = @{}
for ($track = 1; $track -le 60; $track++) {
    $source = Join-Path $AudioPackPath ("dkc_msu-{0}.pcm" -f $track)
    if ($track -eq 25 -and $UseOptionalGangPlankGalleon) {
        $source = Join-Path $AudioPackPath 'Optional - Gangplank Galleon Smash Ultimate Version\dkc_msu-25.pcm'
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing Deluxe MSU-1 track $track`: $source"
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
for ($track = 1; $track -le 60; $track++) {
    $source = $validated[$track]
    $destination = Join-Path $DestinationDirectory ("{0}-{1}.pcm" -f $romBaseName, $track)
    if (Test-Path -LiteralPath $destination) {
        $sourceLength = (Get-Item -LiteralPath $source).Length
        $destinationLength = (Get-Item -LiteralPath $destination).Length
        if ($sourceLength -ne $destinationLength) {
            throw "Existing destination track has a different size: $destination"
        }
        continue
    }
    New-Item -ItemType HardLink -Path $destination -Target $source | Out-Null
    $created++
}

$romHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationRom).Hash
Write-Host "Prepared DKC Deluxe MSU-1 runtime bundle:" -ForegroundColor Green
Write-Host "  ROM:    $destinationRom"
Write-Host "  SHA256: $romHash"
Write-Host "  Marker: $msuPath"
Write-Host "  Tracks: 60 valid ($created new hard links)"
Write-Host "  Track 25: $(if ($UseOptionalGangPlankGalleon) { 'optional Smash Ultimate version' } else { 'main JUD6MENT pack version' })"
