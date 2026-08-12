param([string]$BepInExIl2CppRoot = $env:SUPERZSNES_V0300_ROOT)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($BepInExIl2CppRoot)){throw 'Pass -BepInExIl2CppRoot or set SUPERZSNES_V0300_ROOT.'}
dotnet build (Join-Path $PSScriptRoot 'SuperZSNESSpriteDepthStudioIL2CPP.csproj') -c Release -p:BepInExIl2CppRoot=$BepInExIl2CppRoot
if($LASTEXITCODE){throw 'Runtime plugin build failed.'}
dotnet run --project (Join-Path $PSScriptRoot 'Tests\SpriteDepthStudio.Tests.csproj') -c Release
if($LASTEXITCODE){throw 'Sprite-depth model tests failed.'}
$publish=Join-Path $PSScriptRoot 'Studio\bin\publish'
dotnet publish (Join-Path $PSScriptRoot 'Studio\SpriteDepthStudio.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $publish
if($LASTEXITCODE){throw 'Desktop Studio publish failed.'}
Write-Output ('Built runtime and Studio: '+$publish)
