param(
    [string]$BepInExIl2CppRoot = $env:SUPERZSNES_V0300_ROOT
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BepInExIl2CppRoot)) {
    throw 'Pass -BepInExIl2CppRoot or set SUPERZSNES_V0300_ROOT.'
}
dotnet build (Join-Path $PSScriptRoot 'SuperZSNESNativeAtlasDirtyFixIL2CPP.csproj') `
    -c Release -p:BepInExIl2CppRoot=$BepInExIl2CppRoot
