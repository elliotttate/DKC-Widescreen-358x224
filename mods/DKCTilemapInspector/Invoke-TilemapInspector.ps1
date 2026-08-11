param(
    [ValidateSet('status', 'capture', 'latest')]
    [string]$Command = 'status',
    [string]$Layers = '1,2',
    [string]$Reason = 'powershell-client',
    [string]$GameDir = $env:SUPERZSNES_ROOT
)

$ErrorActionPreference = 'Stop'
$endpointPath = Join-Path $GameDir 'BepInEx\plugins\DKCTilemapInspector\bridge.json'
if (-not (Test-Path -LiteralPath $endpointPath)) {
    throw "Bridge endpoint not found. Start SuperZSNES with the plugin installed: $endpointPath"
}
$endpoint = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $client.Connect([string]$endpoint.host, [int]$endpoint.port)
    $stream = $client.GetStream()
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 8192, $true)
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false), $false, 8192, $true)
    $writer.AutoFlush = $true
    $encode = { param([string]$Text) [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Text)) }
    $parts = @([Guid]::NewGuid().ToString('N'), [string]$endpoint.token, $Command)
    if ($Command -eq 'capture') {
        $parts += @((& $encode 'layers'), (& $encode $Layers), (& $encode 'reason'), (& $encode $Reason))
    }
    $writer.WriteLine(($parts -join "`t"))
    $response = $reader.ReadLine()
    if ($null -eq $response) { throw 'Bridge closed without a response.' }
    $response | ConvertFrom-Json
}
finally {
    if ($null -ne $client) { $client.Dispose() }
}
