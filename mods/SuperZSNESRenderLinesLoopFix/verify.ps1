$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'SuperZSNESRenderLinesLoopFix.csproj'
$tests = Join-Path $root 'Tests\SuperZSNESRenderLinesLoopFix.Tests.csproj'
$game = (Join-Path $env:SUPERZSNES_MANAGED_DIR 'Assembly-CSharp.dll')
$dll = Join-Path $root 'bin\Release\net472\SuperZSNESRenderLinesLoopFix.dll'

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
dotnet run --project $tests -c Release -- $game
if ($LASTEXITCODE -ne 0) { throw 'Offline verifier failed.' }

[pscustomobject]@{
    Verified = $true
    GameAssemblySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $game).Hash
    PluginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash
    InstalledCopies = @((Get-ChildItem -Recurse -Filter 'SuperZSNESRenderLinesLoopFix.dll' -LiteralPath (Join-Path $env:SUPERZSNES_ROOT 'BepInEx\plugins') -ErrorAction SilentlyContinue)).Count
} | ConvertTo-Json
