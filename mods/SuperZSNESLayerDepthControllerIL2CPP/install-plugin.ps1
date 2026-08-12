param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [switch]$Enable3D,
    [switch]$UseWithDkcFramebufferRenderer
)

$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$process = Get-Process SUPERZSNES -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GameRoot*" }
if ($process) { throw "Close SuperZSNES under $GameRoot before installing." }
$source = Join-Path $PSScriptRoot 'bin\Release\net6.0\SuperZSNESLayerDepthControllerIL2CPP.dll'
if (-not (Test-Path -LiteralPath $source)) { throw "Build first: $source" }
$pluginDir = Join-Path $GameRoot 'BepInEx\plugins\SuperZSNESLayerDepthControllerIL2CPP'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
$destination = Join-Path $pluginDir 'SuperZSNESLayerDepthControllerIL2CPP.dll'
Copy-Item -LiteralPath $source -Destination $destination -Force

if ($Enable3D) {
    $config = Join-Path $GameRoot 'BepInEx\config\dev.local.superzsnes.layerdepth.il2cpp.cfg'
    @'
[Controller]
Enabled = true
ActiveAtStartup = true

[Depth]
Separation = 0.5
NeutralBoundary = 6
PlaneGaps = 1,1,1,1,1,1,1,1,1,1,1,1,1
PlaneScales = 1,1,1,1,1,1,1,1,1,1,1,1,1,1
PerspectiveCompensation = true

[ConnectedComponents]
Enabled = true
DepthBands = 7
Spacing = 0.08
MinimumTiles = 2
MaximumAutoTiles = 64
RefreshIntervalFrames = 4

[Controls]
GapStep = 0.1

[Camera]
InitialPitch = 0
InitialYaw = 0
InitialZoom = 0
'@ | Set-Content -LiteralPath $config -Encoding UTF8
}

if ($UseWithDkcFramebufferRenderer) {
    $framebufferConfig = Join-Path $GameRoot 'BepInEx\config\dev.local.superzsnes.dkcframebuffer.il2cpp.cfg'
    if (-not (Test-Path -LiteralPath $framebufferConfig)) {
        throw "Framebuffer config not found: $framebufferConfig"
    }
    $text = Get-Content -Raw -LiteralPath $framebufferConfig
    if ($text -notmatch '(?m)^\s*PresentFramebuffer\s*=') {
        throw 'Framebuffer config has no PresentFramebuffer setting.'
    }
    $text = $text -replace '(?m)^\s*PresentFramebuffer\s*=.*$', 'PresentFramebuffer = false'
    $text = $text -replace '(?m)^\s*ShadowRenderInterval\s*=.*$', 'ShadowRenderInterval = 0'
    Set-Content -LiteralPath $framebufferConfig -Value $text -Encoding UTF8
}

Get-FileHash -Algorithm SHA256 -LiteralPath $destination
