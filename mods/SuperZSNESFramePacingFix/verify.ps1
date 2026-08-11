$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginProject = Join-Path $projectRoot 'SuperZSNESFramePacingFix.csproj'
$testProject = Join-Path $projectRoot 'Tests\SuperZSNESFramePacingFix.Tests.csproj'
$gameAssembly = (Join-Path $env:SUPERZSNES_MANAGED_DIR 'Assembly-CSharp.dll')
$pluginAssembly = Join-Path $projectRoot 'bin\Release\net472\SuperZSNESFramePacingFix.dll'

dotnet build $pluginProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
dotnet run --project $testProject -c Release -- $gameAssembly
if ($LASTEXITCODE -ne 0) { throw 'Offline verifier failed.' }

$gameHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash
$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginAssembly).Hash
[pscustomobject]@{
    Verified = $true
    GameAssemblySha256 = $gameHash
    PluginSha256 = $pluginHash
    InstalledCopies = @((Get-ChildItem -LiteralPath (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins') -Recurse -Filter 'SuperZSNESFramePacingFix.dll' -ErrorAction SilentlyContinue)).Count
} | ConvertTo-Json
