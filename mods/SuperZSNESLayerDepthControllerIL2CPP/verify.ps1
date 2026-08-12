param([string]$BepInExIl2CppRoot = $env:SUPERZSNES_V0300_ROOT)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -BepInExIl2CppRoot $BepInExIl2CppRoot
$interop = Join-Path $BepInExIl2CppRoot 'BepInEx\interop\Assembly-CSharp.dll'
$ilspy = Join-Path $env:USERPROFILE '.dotnet\tools\ilspycmd.exe'
if (-not (Test-Path -LiteralPath $ilspy)) { throw "ilspycmd not found: $ilspy" }
$renderer = (& $ilspy -t PPURenderer $interop | Out-String)
foreach ($required in @('zPositions','zScales','zPositionsBack','zScalesBack',
        'SetupZPositions','UpdateCameras','xRot','yRot','zPos','bgData','usedSprites')) {
    if ($renderer -notmatch [regex]::Escape($required)) { throw "Missing PPURenderer member: $required" }
}
$menu = (& $ilspy -t MainMenuManager $interop | Out-String)
if ($menu -notmatch 'Gimmick3D') { throw 'Gimmick3D enum member is absent.' }
$gameAssembly = Join-Path $BepInExIl2CppRoot 'GameAssembly.dll'
$gameHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash
if ($gameHash -ne '0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A') {
    throw "Unexpected GameAssembly.dll hash: $gameHash"
}
function Read-PeRva([string]$Path, [int]$Rva, [int]$Count) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3C
        $pe = $reader.ReadInt32()
        $stream.Position = $pe + 6
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $stream.Position = $pe + 24 + $optionalSize
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $null = $reader.ReadBytes(8)
            $virtualSize = $reader.ReadUInt32()
            $virtualAddress = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $rawAddress = $reader.ReadUInt32()
            $stream.Position += 16
            if ($Rva -ge $virtualAddress -and
                $Rva -lt ($virtualAddress + [Math]::Max($virtualSize, $rawSize))) {
                $stream.Position = $rawAddress + ($Rva - $virtualAddress)
                return $reader.ReadBytes($Count)
            }
        }
        throw ('RVA 0x{0:X} is not mapped.' -f $Rva)
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}
$scaleActual = Read-PeRva $gameAssembly 0x383674 6
$scaleExpected = [byte[]](0xF3,0x0F,0x10,0x5C,0xB0,0x10)
$zActual = Read-PeRva $gameAssembly 0x383790 9
$zExpected = [byte[]](0x0F,0x28,0xC3,0xF3,0x0F,0x59,0x44,0x91,0x10)
if ([BitConverter]::ToString($scaleActual) -ne [BitConverter]::ToString($scaleExpected) -or
    [BitConverter]::ToString($zActual) -ne [BitConverter]::ToString($zExpected)) {
    throw 'Native DrawLines patch windows do not match the verified v0.300 bytes.'
}
$dll = Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESLayerDepthControllerIL2CPP.dll'
Get-FileHash -Algorithm SHA256 -LiteralPath $dll,$interop,$gameAssembly
Write-Output 'PASS: Gimmick3D fields and both exact native DrawLines depth sites verified.'
