param([string]$BepInExIl2CppRoot = $env:SUPERZSNES_V0300_ROOT)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BepInExIl2CppRoot)) {
    throw 'Pass -BepInExIl2CppRoot or set SUPERZSNES_V0300_ROOT.'
}
dotnet build (Join-Path $PSScriptRoot 'SuperZSNESLayerDepthControllerIL2CPP.csproj') `
    -c Release -p:BepInExIl2CppRoot=$BepInExIl2CppRoot
if ($LASTEXITCODE) { throw 'Layer-depth plugin build failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Tests\SuperZSNESLayerDepthController.Tests.csproj') -c Release
if ($LASTEXITCODE) { throw 'Layer-depth model tests failed.' }
