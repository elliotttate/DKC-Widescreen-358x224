param(
    [Parameter(Mandatory = $true)]
    [string]$RomPath,
    [Parameter(Mandatory = $true)]
    [string]$StatePath,
    [string]$AutomationEndpoint = "",
    [string]$TilemapEndpoint = "",
    [string]$OutputRoot = "",
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $RomPath -PathType Leaf)) { throw "ROM not found: $RomPath" }
if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "State not found: $StatePath" }

$runner = Join-Path $PSScriptRoot "cli\run_regression.py"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Regression runner not found: $runner" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot ("RegressionRuns\suite-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$recipes = @(
    "fresh-jungle-entry-paused",
    "horizontal-right-then-left",
    "vertical-jump-y-boundaries"
)

foreach ($recipe in $recipes) {
    $arguments = @(
        $runner,
        "--recipe", $recipe,
        "--rom", (Resolve-Path -LiteralPath $RomPath).Path,
        "--state", (Resolve-Path -LiteralPath $StatePath).Path,
        "--output", (Join-Path $OutputRoot $recipe)
    )
    if (-not [string]::IsNullOrWhiteSpace($AutomationEndpoint)) { $arguments += @("--automation-endpoint", $AutomationEndpoint) }
    if (-not [string]::IsNullOrWhiteSpace($TilemapEndpoint)) { $arguments += @("--tilemap-endpoint", $TilemapEndpoint) }
    Write-Host "Running $recipe"
    & $Python @arguments
    if ($LASTEXITCODE -ne 0) { throw "Regression recipe '$recipe' failed with exit code $LASTEXITCODE" }
}

Write-Host "Regression suite complete: $OutputRoot"
