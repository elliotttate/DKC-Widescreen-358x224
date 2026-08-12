param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [string]$BepInExIl2CppRoot = $GameRoot
)

$ErrorActionPreference = 'Stop'
$gameAssembly = Join-Path $GameRoot 'GameAssembly.dll'
& (Join-Path $PSScriptRoot 'build.ps1') -BepInExIl2CppRoot $BepInExIl2CppRoot
dotnet run --project (Join-Path $PSScriptRoot 'Tests\SuperZSNESNativeAtlasDirtyFixIL2CPP.Tests.csproj') `
    -c Release -- $gameAssembly
$dll = Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESNativeAtlasDirtyFixIL2CPP.dll'
Get-FileHash -Algorithm SHA256 -LiteralPath $dll,$gameAssembly
