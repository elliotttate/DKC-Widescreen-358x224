$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$forbiddenExtensions = @('.sfc','.smc','.srm','.szst','.state','.sav','.dll','.exe','.pdb','.zip','.7z','.png','.jpg','.jpeg','.bmp','.gif','.jsonl','.csv')
$forbiddenFiles = Get-ChildItem -Recurse -File $root | Where-Object {
    $_.FullName -notmatch '\\(\.git|bin|obj|\.deps|artifacts|__pycache__|\.pytest_cache)\\' -and
    $forbiddenExtensions -contains $_.Extension.ToLowerInvariant()
}
if ($forbiddenFiles) { throw "Forbidden binary/runtime artifacts:`n$($forbiddenFiles.FullName -join "`n")" }

$trackedText = Get-ChildItem -Recurse -File $root | Where-Object {
    $_.FullName -notmatch '\\(\.git|bin|obj|\.deps|artifacts|__pycache__|\.pytest_cache)\\'
}
$localPathPattern = '(?i)([A-Z]:\\Users\\|[A-Z]:\\Downloads\\)'
$credentialPattern = '(?i)(gh[opusr]_[A-Za-z0-9_]{20,}|api[_-]?key\s*[:=]\s*["''][^"'']+|password\s*[:=]\s*["''][^"'']+|secret\s*[:=]\s*["''][^"'']+)'
foreach ($file in $trackedText) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -match $localPathPattern) { throw "Local absolute path found in $($file.FullName)" }
    if ($content -match $credentialPattern) { throw "Possible credential found in $($file.FullName)" }
}

Get-ChildItem -Recurse -File $root -Filter '*.json' | ForEach-Object {
    Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json | Out-Null
}

$parseErrors = @()
Get-ChildItem -Recurse -File $root -Filter '*.ps1' | ForEach-Object {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
    if ($errors) { $script:parseErrors += $errors }
}
if ($parseErrors) { throw "PowerShell parse errors:`n$($parseErrors -join "`n")" }

$pythonFiles = @(Get-ChildItem -Recurse -File $root -Filter '*.py' | Where-Object { $_.FullName -notmatch '\\(\.venv|__pycache__)\\' })
if ($pythonFiles.Count) {
    & python -m py_compile @($pythonFiles.FullName)
    if ($LASTEXITCODE) { throw 'Python source validation failed.' }
}

Write-Host "Source validation passed: $($trackedText.Count) files checked." -ForegroundColor Green
