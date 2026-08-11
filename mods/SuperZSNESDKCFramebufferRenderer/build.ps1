$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SuperZSNESDKCFramebufferRenderer.csproj'
dotnet build $project -c Release
