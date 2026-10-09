<#
.SYNOPSIS
Local read-only validator/runner for M0 CI reporting: workflow, gitignore, category source, and optional TRX summary.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$TrxDirectory,
    [string]$SummaryOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$assert = Join-Path $PSScriptRoot 'Assert-CiReportingConfig.ps1'
& $assert -RepoRoot $RepoRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($TrxDirectory) {
    $write = Join-Path $PSScriptRoot 'Write-TestCategorySummary.ps1'
    $args = @{ TrxDirectory = $TrxDirectory }
    if ($SummaryOutputPath) { $args.OutputPath = $SummaryOutputPath }
    & $write @args
    exit $LASTEXITCODE
}

exit 0
