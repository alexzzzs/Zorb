param(
    [Parameter(Position = 0)]
    [string]$OutputDir,
    [switch]$RecoveryCSharp,
    [string]$Seed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$Python = if ($env:PYTHON) { $env:PYTHON } else { "python" }
$Arguments = @((Join-Path $RootDir "scripts/bootstrap_compiler.py"), "publish")
if ($OutputDir) {
    $Arguments += $OutputDir
}
if ($RecoveryCSharp) {
    $Arguments += "--recovery-csharp"
}
if ($Seed) {
    $Arguments += @("--seed", $Seed)
}

& $Python @Arguments
if ($LASTEXITCODE -ne 0) {
    throw "Cross-platform Zorb publisher failed with exit code $LASTEXITCODE."
}
