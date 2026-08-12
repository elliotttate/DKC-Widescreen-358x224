param([Parameter(Mandatory=$true)][string]$EmulatorRoot)
$ErrorActionPreference='Stop'
$resolved=(Resolve-Path -LiteralPath $EmulatorRoot).Path
$running=Get-Process SUPERZSNES -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($resolved,[StringComparison]::OrdinalIgnoreCase)}
if($running){throw 'Close this SuperZSNES instance before installing Object Depth Studio.'}
& (Join-Path $PSScriptRoot 'build.ps1') -BepInExIl2CppRoot $resolved
$target=Join-Path $resolved 'BepInEx\plugins\SuperZSNESSpriteDepthStudioIL2CPP'
$studio=Join-Path $target 'Studio'
New-Item -ItemType Directory -Force -Path $target,$studio | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESSpriteDepthStudioIL2CPP.dll') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Studio\bin\publish\SpriteDepthStudio.exe') -Destination $studio -Force
@"
param([string]`$Root = '$($target.Replace("'","''"))')
Start-Process -FilePath (Join-Path `$Root 'Studio\SpriteDepthStudio.exe') -ArgumentList '--root', ('"'+`$Root+'"')
"@ | Set-Content -LiteralPath (Join-Path $target 'launch-studio.ps1') -Encoding UTF8
Write-Output ('Installed Object Depth Studio to '+$target)
