$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build.ps1')
dotnet run --project (Join-Path $root 'Tests\SuperZSNESDKCFramebufferRenderer.Tests.csproj') -c Release
$dll = Join-Path $root 'bin\Release\net472\SuperZSNESDKCFramebufferRenderer.dll'
$game = (Join-Path $env:SUPERZSNES_MANAGED_DIR 'Assembly-CSharp.dll')
$installed = (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins\SuperZSNESDKCFramebufferRenderer\SuperZSNESDKCFramebufferRenderer.dll')
Get-FileHash -Algorithm SHA256 -LiteralPath $dll,$game
if (Test-Path -LiteralPath $installed) {
  Write-Output 'Installed copy exists:'
  Get-FileHash -Algorithm SHA256 -LiteralPath $installed
} else {
  Write-Output 'InstalledCopies=0'
}
