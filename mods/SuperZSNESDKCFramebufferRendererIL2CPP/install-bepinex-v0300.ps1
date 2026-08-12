param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$expectedExe = 'B83358E453C9378A37AA0E43D22886AD49EE426F1ECF381B4F84A3A49F54FDD6'
$expectedGameAssembly = '0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A'
$expectedPackage = 'AEA68423FE7539DEAC6102B4CF9F5EE4205519EB92533FE904500F74B0D3DAAE'

$exe = Join-Path $GameRoot 'SUPERZSNES.exe'
$gameAssembly = Join-Path $GameRoot 'GameAssembly.dll'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash -ne $expectedExe -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash -ne $expectedGameAssembly) {
    throw 'The target is not the verified 32-bit SuperZSNES v0.300 IL2CPP build.'
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash -ne $expectedPackage) {
    throw 'The BepInEx package is not the pinned Unity.IL2CPP-win-x86 be.783 archive.'
}
if (Get-Process SUPERZSNES -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GameRoot*" }) {
    throw "Close SuperZSNES before installing into $GameRoot"
}

$targets = @('BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')
$existing = @($targets | Where-Object { Test-Path -LiteralPath (Join-Path $GameRoot $_) })
if ($existing.Count -ne 0) {
    throw ('Refusing to overwrite an existing BepInEx installation: ' + ($existing -join ', '))
}

Expand-Archive -LiteralPath $PackagePath -DestinationPath $GameRoot
Write-Output "Installed pinned BepInEx IL2CPP x86 be.783 into $GameRoot"
