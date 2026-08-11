param(
    [string]$GameDir = $env:SUPERZSNES_ROOT
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:SUPERZSNES_GAME_DIR = (Resolve-Path -LiteralPath $GameDir).Path
uv run --project $serverDir superzsnes-mcp
