param(
    [string]$Configuration = 'Release',
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

$ErrorActionPreference = 'Stop'

$forward = @{
    Configuration = $Configuration
    Scenario      = 'Acceptance'
}

foreach ($key in @('CredentialsPath', 'Endpoint', 'Bucket', 'ResultsDirectory', 'TrxLogFileName', 'SanitizedReportFileName', 'BlameHangTimeout', 'SelfTestExitCode')) {
    if ($PSBoundParameters.ContainsKey($key) -and $null -ne $PSBoundParameters[$key] -and "$($PSBoundParameters[$key])" -ne '') {
        $forward[$key] = $PSBoundParameters[$key]
    }
}

if ($NoRestore) { $forward['NoRestore'] = $true }
if ($NoBuild) { $forward['NoBuild'] = $true }
if ($ProtocolSelfTest) { $forward['ProtocolSelfTest'] = $true }

& (Join-Path $PSScriptRoot 'Run-YandexCloudLiveSmoke.ps1') @forward
exit $LASTEXITCODE
