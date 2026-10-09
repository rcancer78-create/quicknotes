param(
    [string]$Configuration = 'Release',
    [ValidateSet('Smoke', 'Acceptance')]
    [string]$Scenario = 'Smoke',
    [string]$CredentialsPath,
    [string]$Endpoint,
    [string]$Bucket,
    [string]$ResultsDirectory,
    [string]$TrxLogFileName,
    [string]$SanitizedReportFileName,
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$BlameHangTimeout,
    [switch]$ProtocolSelfTest,
    [int]$SelfTestExitCode = 0
)

# Sanitized Markdown protocol/report is opt-in: both ResultsDirectory and
# SanitizedReportFileName must be supplied. Ordinary local runs without those
# parameters do not create a report file.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$protocolScript = Join-Path $PSScriptRoot 'CloudRunProtocol.ps1'
# GitHub CI runs on PowerShell 7; local machines without pwsh fall back to
# Windows PowerShell 5.1. The protocol script is 5.1-compatible.
$protocolHost = 'powershell'
if ($null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)) { $protocolHost = 'pwsh' }
$protocolOptIn = -not [string]::IsNullOrWhiteSpace($ResultsDirectory) -and -not [string]::IsNullOrWhiteSpace($SanitizedReportFileName)
$protocolStartedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$protocolRunId = [guid]::NewGuid().ToString('D')
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$testExit = 1
$actual = 'not-started'
$state = 'planned'
$outcome = 'unknown'
$cleanupAttempted = 'no'
$cleanupResult = 'not-run'
$envTouched = $false
$failureMessage = $null

function Invoke-CloudRunProtocolWrite {
    param(
        [string]$WriteState,
        [string]$WriteOutcome,
        [string]$WriteActual,
        [string]$WriteCleanupAttempted,
        [string]$WriteCleanupResult
    )
    if (-not $protocolOptIn) {
        return
    }
    # Isolated -File process: CloudRunProtocol.ps1 uses exit, which would
    # otherwise terminate this runner and skip credential cleanup.
    $writeArgs = @(
        '-NoProfile', '-File', $protocolScript,
        '-Action', 'Write',
        '-ResultsDirectory', $ResultsDirectory,
        '-SanitizedReportFileName', $SanitizedReportFileName,
        '-State', $WriteState,
        '-Outcome', $WriteOutcome,
        '-Scenario', $Scenario,
        '-RunId', $protocolRunId,
        '-RepoRoot', $repoRoot,
        '-Configuration', $Configuration,
        '-StartedUtc', $protocolStartedUtc,
        '-DurationMs', [string]$sw.ElapsedMilliseconds,
        '-CleanupAttempted', $WriteCleanupAttempted,
        '-CleanupResult', $WriteCleanupResult,
        '-ActualResult', $WriteActual
    )
    # Planned/running reports have no numeric exit code yet. Omit the argument
    # entirely rather than pass an empty value, which legacy hosts (Windows
    # PowerShell 5.1) drop, binding -ExitCode with no argument.
    if ("$testExit" -ne '') {
        $writeArgs += @('-ExitCode', [string]$testExit)
    }
    if (-not [string]::IsNullOrWhiteSpace($Endpoint)) {
        $writeArgs += @('-Endpoint', $Endpoint)
    }
    if (-not [string]::IsNullOrWhiteSpace($Bucket)) {
        $writeArgs += @('-Bucket', $Bucket)
    }
    if (-not [string]::IsNullOrWhiteSpace($TrxLogFileName)) {
        $writeArgs += @('-TrxLogFileName', $TrxLogFileName)
    }
    & $protocolHost @writeArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Sanitized protocol report update failed.'
    }
}

try {
    if ($protocolOptIn) {
        $testExit = ''
        Invoke-CloudRunProtocolWrite -WriteState 'planned' -WriteOutcome 'unknown' -WriteActual 'planned' -WriteCleanupAttempted 'no' -WriteCleanupResult 'not-run'
        $testExit = 1
    }

    if (-not $ProtocolSelfTest) {
        if ($CredentialsPath) {
            if (-not (Test-Path -LiteralPath $CredentialsPath -PathType Leaf)) {
                $actual = 'setup-failed: credentials-or-connection-metadata-missing'
                $state = 'failed'
                $outcome = 'failed'
                throw 'DPAPI credential file is missing.'
            }
            if ([string]::IsNullOrWhiteSpace($Endpoint) -or [string]::IsNullOrWhiteSpace($Bucket)) {
                $actual = 'setup-failed: endpoint-or-bucket-missing'
                $state = 'failed'
                $outcome = 'failed'
                throw 'CredentialsPath requires Endpoint and Bucket.'
            }

            $env:QUICKNOTES_LIVE_S3_REQUIRED = '1'
            $env:QUICKNOTES_LIVE_S3_CREDENTIALS_PATH = $CredentialsPath
            $env:QUICKNOTES_LIVE_S3_ENDPOINT = [string]$Endpoint
            $env:QUICKNOTES_LIVE_S3_BUCKET = [string]$Bucket
            $envTouched = $true
        }
        else {
            $secureRoot = Join-Path $env:LOCALAPPDATA 'QuickNotesCloudSmoke'
            $connectionPath = Join-Path $secureRoot 'connection.json'

            if (-not (Test-Path -LiteralPath $connectionPath -PathType Leaf)) {
                $actual = 'setup-failed: credentials-or-connection-metadata-missing'
                $state = 'failed'
                $outcome = 'failed'
                throw 'Cloud smoke connection metadata is missing.'
            }

            $connection = Get-Content -Raw -LiteralPath $connectionPath | ConvertFrom-Json
            $localCredentialsPath = Join-Path $secureRoot $connection.credentialsFile
            if (-not (Test-Path -LiteralPath $localCredentialsPath -PathType Leaf)) {
                $actual = 'setup-failed: credentials-or-connection-metadata-missing'
                $state = 'failed'
                $outcome = 'failed'
                throw 'DPAPI credential file is missing.'
            }

            $env:QUICKNOTES_LIVE_S3_REQUIRED = '1'
            $env:QUICKNOTES_LIVE_S3_CREDENTIALS_PATH = $localCredentialsPath
            $env:QUICKNOTES_LIVE_S3_ENDPOINT = [string]$connection.endpoint
            $env:QUICKNOTES_LIVE_S3_BUCKET = [string]$connection.bucket
            $envTouched = $true
            if ([string]::IsNullOrWhiteSpace($Endpoint)) {
                $Endpoint = [string]$connection.endpoint
            }
            if ([string]::IsNullOrWhiteSpace($Bucket)) {
                $Bucket = [string]$connection.bucket
            }
        }
    }

    $state = 'running'
    $actual = 'running'
    if ($protocolOptIn) {
        $testExit = ''
        Invoke-CloudRunProtocolWrite -WriteState 'running' -WriteOutcome 'unknown' -WriteActual 'running' -WriteCleanupAttempted 'no' -WriteCleanupResult 'not-run'
        $testExit = 1
    }

    if ($ProtocolSelfTest) {
        $testExit = $SelfTestExitCode
    }
    else {
        $filter = if ($Scenario -eq 'Acceptance') {
            'FullyQualifiedName~YandexObjectStorageLiveAcceptanceTests.OptIn_TwoDevice_ProductionSync_KeepBothTombstoneAndProtectedScan'
        } else {
            'FullyQualifiedName~YandexObjectStorageLiveSmokeTests.OptIn_PutGetDelete_RoundTrip'
        }

        $testArgs = @(
            (Join-Path $repoRoot 'QuickNotes.Tests\QuickNotes.Tests.csproj'),
            '-c', $Configuration,
            '--nologo',
            '--filter', $filter,
            '--logger', 'console;verbosity=normal'
        )

        if ($NoRestore) { $testArgs += '--no-restore' }
        if ($NoBuild) { $testArgs += '--no-build' }
        if ($BlameHangTimeout) {
            $testArgs += '--blame-hang'
            $testArgs += '--blame-hang-timeout'
            $testArgs += $BlameHangTimeout
        }
        if ($ResultsDirectory) {
            $testArgs += '--results-directory'
            $testArgs += $ResultsDirectory
        }
        if ($TrxLogFileName) {
            $testArgs += '--logger'
            $testArgs += "trx;LogFileName=$TrxLogFileName"
        }

        & dotnet test @testArgs
        $testExit = $LASTEXITCODE
    }

    if ($testExit -eq 0) {
        $state = 'passed'
        $outcome = 'passed'
        $actual = 'passed'
    }
    else {
        $state = 'failed'
        $outcome = 'failed'
        $actual = 'failed'
    }

}
catch {
    $failureMessage = if ($actual.StartsWith('setup-failed:', [StringComparison]::Ordinal)) {
        $actual
    } else {
        'cloud test runner failed'
    }
    $testExit = 1
    if ($state -ne 'failed') {
        $state = 'failed'
        $outcome = 'failed'
    }
    if ($actual -eq 'not-started' -or $actual -eq 'planned' -or $actual -eq 'running') {
        $actual = 'failed'
    }
}
finally {
    $sw.Stop()
    $cleanupAttempted = 'yes'
    try {
        Remove-Item Env:QUICKNOTES_LIVE_S3_REQUIRED -ErrorAction SilentlyContinue
        Remove-Item Env:QUICKNOTES_LIVE_S3_CREDENTIALS_PATH -ErrorAction SilentlyContinue
        Remove-Item Env:QUICKNOTES_LIVE_S3_ENDPOINT -ErrorAction SilentlyContinue
        Remove-Item Env:QUICKNOTES_LIVE_S3_BUCKET -ErrorAction SilentlyContinue
        if ($envTouched) {
            $cleanupResult = 'process-env-cleared'
        }
        else {
            $cleanupResult = 'not-applicable'
        }
    }
    catch {
        $cleanupResult = 'cleanup-error'
    }

    if ($protocolOptIn) {
        if ($state -eq 'passed' -or $state -eq 'failed') {
            try {
                Invoke-CloudRunProtocolWrite -WriteState $state -WriteOutcome $outcome -WriteActual $actual -WriteCleanupAttempted 'no' -WriteCleanupResult 'not-run'
            }
            catch {
            }
        }
        try {
            Invoke-CloudRunProtocolWrite -WriteState 'cleanup-attempted' -WriteOutcome $outcome -WriteActual $actual -WriteCleanupAttempted $cleanupAttempted -WriteCleanupResult $cleanupResult
        }
        catch {
        }
    }
}

if ($null -eq $testExit -or "$testExit" -eq '') {
    $testExit = 1
}
if ($failureMessage) {
    Write-Error $failureMessage
}
exit [int]$testExit
