param(
    [ValidateSet('arm', 'disarm', 'toggle', 'status', 'mark')]
    [string]$Command = 'status',
    [string]$Message = '',
    [string]$GameDir = $env:SUPERZSNES_ROOT,
    [int]$TimeoutMilliseconds = 3000
)

$ErrorActionPreference = 'Stop'
$controlDirectory = Join-Path $GameDir 'BepInEx\plugins\DKCTileStreamTracer\control'
$commandPath = Join-Path $controlDirectory 'command.txt'
$statusPath = Join-Path $controlDirectory 'status.json'
New-Item -ItemType Directory -Path $controlDirectory -Force | Out-Null

$payload = if ($Command -eq 'mark') { "mark $Message" } else { $Command }
$previousWrite = if (Test-Path -LiteralPath $statusPath) { (Get-Item -LiteralPath $statusPath).LastWriteTimeUtc } else { [datetime]::MinValue }
Set-Content -LiteralPath $commandPath -Value $payload -NoNewline

$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Milliseconds 50
    if (Test-Path -LiteralPath $statusPath) {
        $item = Get-Item -LiteralPath $statusPath
        if ($item.LastWriteTimeUtc -gt $previousWrite) {
            Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
            exit 0
        }
    }
} while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds)

throw "The tracer did not update status within $TimeoutMilliseconds ms. Confirm SuperZSNES is running and the plugin loaded."
