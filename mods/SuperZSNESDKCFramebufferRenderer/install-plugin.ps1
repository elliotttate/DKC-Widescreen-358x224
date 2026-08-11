$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'bin\Release\net472\SuperZSNESDKCFramebufferRenderer.dll'
$targetDirectory = (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins\SuperZSNESDKCFramebufferRenderer')
if (-not (Test-Path -LiteralPath $source)) { throw "Build output not found: $source" }
if (Get-Process -Name 'SUPERZSNES' -ErrorAction SilentlyContinue) { throw 'Close SuperZSNES before installing this plugin.' }
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $targetDirectory 'SuperZSNESDKCFramebufferRenderer.dll') -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $source,(Join-Path $targetDirectory 'SuperZSNESDKCFramebufferRenderer.dll')
