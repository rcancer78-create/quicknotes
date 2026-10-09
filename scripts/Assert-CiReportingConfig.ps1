<#
.SYNOPSIS
Read-only local proof that CI category reporting, TRX artifact rules, and LiveCloud isolation are configured.
Does not call GitHub and does not require credentials.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$errors = New-Object System.Collections.Generic.List[string]
function Add-Err([string]$Message) { [void]$errors.Add($Message) }

$workflow = Join-Path $RepoRoot '.github\workflows\ci.yml'
$gitignore = Join-Path $RepoRoot '.gitignore'
$summary = Join-Path $RepoRoot 'scripts\Write-TestCategorySummary.ps1'
$categories = Join-Path $RepoRoot 'QuickNotes.Tests\TestCategories.cs'

foreach ($path in @($workflow, $gitignore, $summary, $categories)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Add-Err "Missing required file: $path"
    }
}

if (Test-Path -LiteralPath $workflow) {
    $yml = Get-Content -LiteralPath $workflow -Raw

    if ($yml -notmatch 'runs-on:\s*windows-latest') { Add-Err 'CI runner is not windows-latest.' }
    if ($yml -notmatch 'timeout-minutes:') { Add-Err 'CI job timeout-minutes is missing.' }
    if ($yml -notmatch '--blame-hang') { Add-Err 'Hang protection --blame-hang is missing.' }
    if ($yml -notmatch '--blame-hang-timeout') { Add-Err 'Hang timeout --blame-hang-timeout is missing.' }
    if ($yml -notmatch 'dotnet test') { Add-Err 'CI does not run dotnet test.' }
    if ($yml -match '--filter') { Add-Err 'Authoritative test run must not use --filter.' }
    if ($yml -notmatch 'results-directory') { Add-Err 'TRX results-directory is missing.' }
    if ($yml -notmatch 'artifacts[/\\]ci') { Add-Err 'TRX results must stay under artifacts/ci, not the repo root.' }
    if ($yml -notmatch 'if:\s*always\(\)') { Add-Err 'Artifact upload must use if: always().' }
    if ($yml -notmatch 'retention-days:\s*(\d+)') {
        Add-Err 'Artifact retention-days is missing.'
    }
    else {
        $days = [int]$Matches[1]
        if ($days -lt 1 -or $days -gt 14) {
            Add-Err "Artifact retention-days=$days is not a short window (1-14)."
        }
    }
    if ($yml -notmatch 'actions/upload-artifact') { Add-Err 'CI does not upload a TRX artifact.' }
    if ($yml -match 'secrets\.') { Add-Err 'CI workflow must not reference GitHub secrets.' }
    if ($yml -match 'QUICKNOTES_LIVE_S3') { Add-Err 'Ordinary CI must not set LiveCloud environment variables.' }
    if ($yml -match 'continue-on-error:\s*true') { Add-Err 'CI must not continue-on-error over a failing test step.' }

    $restoreAt = $yml.IndexOf('dotnet restore')
    $formatAt = $yml.IndexOf('dotnet format whitespace')
    $buildAt = $yml.IndexOf('dotnet build')
    if ($formatAt -lt 0) {
        Add-Err 'CI must run dotnet format whitespace --verify-no-changes after restore and before build.'
    }
    else {
        if ($yml -notmatch 'dotnet format whitespace[^\r\n]*--verify-no-changes') {
            Add-Err 'Whitespace format gate must pass --verify-no-changes.'
        }
        if ($yml -notmatch 'dotnet format whitespace[^\r\n]*--no-restore') {
            Add-Err 'Whitespace format gate must pass --no-restore after the restore step.'
        }
        if ($restoreAt -lt 0 -or $formatAt -lt $restoreAt) {
            Add-Err 'Format verify must run after restore.'
        }
        if ($buildAt -lt 0 -or $formatAt -gt $buildAt) {
            Add-Err 'Format verify must run before build.'
        }
    }
}

if (Test-Path -LiteralPath $gitignore) {
    $ignore = Get-Content -LiteralPath $gitignore -Raw
    if ($ignore -notmatch '(?m)^TestResults/') { Add-Err '.gitignore must ignore TestResults/.' }
    if ($ignore -notmatch '(?m)^\*\.trx') { Add-Err '.gitignore must ignore *.trx.' }
    if ($ignore -notmatch '(?m)^artifacts/') { Add-Err '.gitignore must ignore artifacts/.' }
}

if (Test-Path -LiteralPath $categories) {
    $src = Get-Content -LiteralPath $categories -Raw
    foreach ($token in @('Unit', 'Integration', 'UiSensitive', 'LiveCloud')) {
        if ($src -notmatch [regex]::Escape("public const string $token")) {
            Add-Err "TestCategories.cs is missing $token."
        }
    }
}

if (Test-Path -LiteralPath $summary) {
    $sum = Get-Content -LiteralPath $summary -Raw
    if ($sum -notmatch 'cannot hide failing tests') { Add-Err 'Category summary script must fail closed on test failures.' }
    if ($sum -notmatch 'LiveCloud') { Add-Err 'Category summary script must mention LiveCloud.' }
}

if ($errors.Count -gt 0) {
    Write-Output 'CI reporting configuration is NOT proven:'
    $errors | ForEach-Object { Write-Output " - $_" }
    exit 1
}

Write-Output 'CI reporting configuration is locally proven without GitHub credentials.'
Write-Output 'External boundary still open: first GitHub run and branch protection cannot be asserted from this repo clone.'
exit 0
