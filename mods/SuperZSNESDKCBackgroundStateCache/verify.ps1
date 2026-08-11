$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'SuperZSNESDKCBackgroundStateCache.csproj'
$tests = Join-Path $root 'Tests\SuperZSNESDKCBackgroundStateCache.Tests.csproj'
$game = (Join-Path $env:SUPERZSNES_MANAGED_DIR 'Assembly-CSharp.dll')
$dll = Join-Path $root 'bin\Release\net472\SuperZSNESDKCBackgroundStateCache.dll'

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
dotnet run --project $tests -c Release -- $game
if ($LASTEXITCODE -ne 0) { throw 'Offline verifier failed.' }

$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash
$installed = @(Get-ChildItem -Recurse -Filter 'SuperZSNESDKCBackgroundStateCache.dll' -LiteralPath (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins') -ErrorAction SilentlyContinue)
[pscustomobject]@{
    Verified = $true
    GameAssemblySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $game).Hash
    PluginSha256 = $pluginHash
    InstalledFilenameCopies = $installed.Count
    InstalledMatchingHashCopies = @($installed | Where-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $pluginHash }).Count
} | ConvertTo-Json
