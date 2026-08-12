param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [switch]$EnablePresentation
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESDKCFramebufferRendererIL2CPP.dll'
if (-not (Test-Path -LiteralPath $source)) {
    throw "Build the Release plugin first: $source"
}
if (Get-Process SUPERZSNES -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GameRoot*" }) {
    throw "Close the disposable v0.300 emulator before installing into $GameRoot"
}

$pluginDirectory = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESDKCFramebufferRendererIL2CPP'
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $pluginDirectory 'SuperZSNESDKCFramebufferRendererIL2CPP.dll') -Force

if ($EnablePresentation) {
    $configPath = Join-Path $GameRoot 'BepInEx\config\dev.local.superzsnes.dkcframebuffer.il2cpp.cfg'
    New-Item -ItemType Directory -Path (Split-Path $configPath) -Force | Out-Null
    @'
[Renderer]

Enabled = true
PresentFramebuffer = true
ShadowRenderInterval = 0
RetainedBackgrounds = true

[Geometry]

AutoDetectRomGeometry = true
Width = 358
Height = 224
LeftExtension = 51
'@ | Set-Content -LiteralPath $configPath -Encoding UTF8
}

Get-FileHash -Algorithm SHA256 (Join-Path $pluginDirectory 'SuperZSNESDKCFramebufferRendererIL2CPP.dll')
