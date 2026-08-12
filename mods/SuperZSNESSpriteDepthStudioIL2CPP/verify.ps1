param([string]$BepInExIl2CppRoot = $env:SUPERZSNES_V0300_ROOT)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -BepInExIl2CppRoot $BepInExIl2CppRoot
$game=Join-Path $BepInExIl2CppRoot 'GameAssembly.dll'
if((Get-FileHash -Algorithm SHA256 -LiteralPath $game).Hash -ne '0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A'){throw 'Unexpected GameAssembly hash.'}
function Read-PeRva([string]$Path,[int]$Rva,[int]$Count){
 $s=[IO.File]::OpenRead($Path);$r=[IO.BinaryReader]::new($s);try{$s.Position=0x3C;$pe=$r.ReadInt32();$s.Position=$pe+6;$n=$r.ReadUInt16();$s.Position=$pe+20;$os=$r.ReadUInt16();$s.Position=$pe+24+$os;for($i=0;$i-lt$n;$i++){$null=$r.ReadBytes(8);$vs=$r.ReadUInt32();$va=$r.ReadUInt32();$rs=$r.ReadUInt32();$ra=$r.ReadUInt32();$s.Position+=16;if($Rva-ge$va-and$Rva-lt($va+[Math]::Max($vs,$rs))){$s.Position=$ra+($Rva-$va);return $r.ReadBytes($Count)}}throw ('Unmapped RVA 0x{0:X}' -f $Rva)}finally{$r.Dispose();$s.Dispose()}}
$scale=Read-PeRva $game 0x3925ED 5;$z=Read-PeRva $game 0x393C74 5
if([BitConverter]::ToString($scale)-ne'F3-0F-11-45-98'-or[BitConverter]::ToString($z)-ne'F3-0F-58-4D-90'){throw 'RenderLines hook windows do not match.'}
$dll=Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESSpriteDepthStudioIL2CPP.dll';$exe=Join-Path $PSScriptRoot 'Studio\bin\publish\SpriteDepthStudio.exe'
Get-FileHash -Algorithm SHA256 -LiteralPath $dll,$exe,$game
Write-Output 'PASS: exact RenderLines sites, sprite/background decoders, component profiles, and desktop Object Studio verified.'
