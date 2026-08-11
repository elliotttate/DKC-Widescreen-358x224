param(
    [string]$BepInExRoot = $env:BEPINEX_ROOT,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = (Resolve-Path -LiteralPath (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
$packageDir = Join-Path $projectDir 'package'
$releaseZip = Join-Path (Split-Path -Parent $projectDir) 'DKCWidescreenDebugger-v0.1.0-BepInEx5-x86.zip'
$sourceDll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\DKCWidescreenDebugger.dll"
$pluginDir = Join-Path $packageDir 'BepInEx\plugins\DKCWidescreenDebugger'
$mcpOutput = Join-Path $pluginDir 'mcp_server'

if (-not (Test-Path -LiteralPath $sourceDll)) { throw "Build output not found: $sourceDll" }
if (-not (Test-Path -LiteralPath (Join-Path $BepInExRoot 'winhttp.dll'))) { throw "Not a complete BepInEx Windows package: $BepInExRoot" }

if (Test-Path -LiteralPath $packageDir) {
    $resolvedPackage = (Resolve-Path -LiteralPath $packageDir).Path
    if (-not $resolvedPackage.StartsWith($projectDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedPackage) -ne 'package') {
        throw "Refusing to remove unexpected package path: $resolvedPackage"
    }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDir | Out-Null
Copy-Item -Path (Join-Path $BepInExRoot '*') -Destination $packageDir -Recurse -Force
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $pluginDir 'DKCWidescreenDebugger.dll')
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination (Join-Path $pluginDir 'README.md')

New-Item -ItemType Directory -Path (Join-Path $mcpOutput 'src\superzsnes_mcp') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDir 'mcp_server\pyproject.toml') -Destination $mcpOutput
Copy-Item -LiteralPath (Join-Path $projectDir 'mcp_server\README.md') -Destination $mcpOutput
Copy-Item -LiteralPath (Join-Path $projectDir 'mcp_server\run-mcp.ps1') -Destination $mcpOutput
if (Test-Path -LiteralPath (Join-Path $projectDir 'mcp_server\uv.lock')) {
    Copy-Item -LiteralPath (Join-Path $projectDir 'mcp_server\uv.lock') -Destination $mcpOutput
}
Get-ChildItem -LiteralPath (Join-Path $projectDir 'mcp_server\src\superzsnes_mcp') -Filter '*.py' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $mcpOutput 'src\superzsnes_mcp')
}
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination (Join-Path $packageDir 'DKCWidescreenDebugger_README.md')

if (Test-Path -LiteralPath $releaseZip) { Remove-Item -LiteralPath $releaseZip -Force }
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $releaseZip -CompressionLevel Optimal
Write-Host "Created $releaseZip"
