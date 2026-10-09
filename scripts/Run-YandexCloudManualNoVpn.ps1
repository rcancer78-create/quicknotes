param(
    [ValidateSet('Smoke', 'Acceptance', 'All')]
    [string]$Scenario = 'All',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Run-YandexCloudLiveSmoke.ps1'

if ($Scenario -in @('Smoke', 'All')) {
    & $runner -Configuration $Configuration -Scenario Smoke
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if ($Scenario -in @('Acceptance', 'All')) {
    & $runner -Configuration $Configuration -Scenario Acceptance
    exit $LASTEXITCODE
}

exit 0
