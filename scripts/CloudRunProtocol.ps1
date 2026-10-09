<#
.SYNOPSIS
Reusable sanitized cloud-stand protocol/report layer for live runners.

Writes a run-specific Markdown manifest under an explicit results directory.
Does not call the network. Does not record secrets, credential paths, raw
bucket names, endpoint query/userinfo, environment dumps, object/note text,
recovery material, or exception dumps.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Write', 'Fingerprint', 'SanitizeEndpoint')]
    [string]$Action,

    [string]$ResultsDirectory,
    [string]$SanitizedReportFileName,
    [string]$State,
    [string]$Outcome,
    [string]$Scenario,
    [string]$RunId,
    [string]$RepoRoot,
    [string]$Configuration = 'Release',
    [string]$Endpoint,
    [string]$Bucket,
    [string]$ExpectedFilter,
    [string]$TrxLogFileName,
    [string]$StartedUtc,
    [string]$UpdatedUtc,
    [string]$FinishedUtc,
    [string]$ExitCode,
    [string]$DurationMs,
    [string]$CleanupAttempted,
    [string]$CleanupResult,
    [string]$ActualResult,
    [string]$CommitId,
    [string]$AppVersion,
    [string]$DotNetSdkVersion,
    [string]$RuntimeVersion,
    [string]$AwsSdkVersion,
    [string]$OsDescription,
    [string]$Architecture,
    [string]$TestAssembly,
    [string]$PrefixContract
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-QnNormalizedFullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-QnPathIsUnder([string]$Candidate, [string]$Ancestor) {
    $c = (Get-QnNormalizedFullPath $Candidate).TrimEnd('\', '/')
    $a = (Get-QnNormalizedFullPath $Ancestor).TrimEnd('\', '/')
    if ($c.Equals($a, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $sep = [IO.Path]::DirectorySeparatorChar
    $alt = [IO.Path]::AltDirectorySeparatorChar
    return $c.StartsWith($a + $sep, [StringComparison]::OrdinalIgnoreCase) -or
        $c.StartsWith($a + $alt, [StringComparison]::OrdinalIgnoreCase)
}

function Get-QnLiveProfileDirectory {
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($local)) {
        $local = $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($local)) {
        throw 'LOCALAPPDATA is not available; refusing protocol write.'
    }
    return Get-QnNormalizedFullPath (Join-Path $local 'QuickNotes')
}

function Test-QnForbiddenMarker([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) {
        return $false
    }
    if ($Text -match '(?i)AKIA[0-9A-Z]{16}') { return $true }
    if ($Text -match '(?i)SecretAccessKey') { return $true }
    if ($Text -match '(?i)AWS_SECRET_ACCESS_KEY') { return $true }
    if ($Text -match '(?i)X-Amz-(Signature|Credential|Security-Token)') { return $true }
    if ($Text -match '(?i)AWSAccessKeyId') { return $true }
    if ($Text -match '(?i)QUICKNOTES_LIVE_S3_CREDENTIALS') { return $true }
    if ($Text -match '(?i)(creds\.dat|credentials\.dat|connection\.json)') { return $true }
    if ($Text -match '(?i)qn-cloud-creds') { return $true }
    if ($Text -match '(?i)password\s*[=:]') { return $true }
    if ($Text -match '(?i)\bnote\s*=') { return $true }
    if ($Text -match '(?i)recovery[-_ ]?(key|material|wrap)') { return $true }
    if ($Text -match '(?i)https?://[^/\s"'']+:[^@/\s"'']+@') { return $true }
    if ($Text -match '(?i)(at [A-Za-z0-9_.]+\.[A-Za-z0-9_]+\[0x)') { return $true }
    return $false
}

function Get-QnCloudBucketFingerprint([string]$Bucket) {
    if ([string]::IsNullOrWhiteSpace($Bucket)) {
        return '[bucket-omitted]'
    }
    $normalized = $Bucket.Trim()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
        $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
        return "sha256:$hex"
    }
    finally {
        $sha.Dispose()
    }
}

function Get-QnCloudEndpointHost([string]$Endpoint) {
    if ([string]::IsNullOrWhiteSpace($Endpoint)) {
        return '[endpoint-omitted]'
    }
    $raw = $Endpoint.Trim()
    $candidate = $raw
    if ($candidate -notmatch '^[a-zA-Z][a-zA-Z0-9+\.\-]*://') {
        $candidate = 'https://' + $candidate
    }
    try {
        $uri = [Uri]$candidate
        if (-not $uri.IsAbsoluteUri) {
            return '[invalid-endpoint]'
        }
        if ([string]::IsNullOrWhiteSpace($uri.Host)) {
            return '[invalid-endpoint]'
        }
        return $uri.Host
    }
    catch {
        return '[invalid-endpoint]'
    }
}

function Get-QnSafeCell([string]$Value) {
    if ($null -eq $Value) {
        return ''
    }
    $text = [string]$Value
    $text = $text -replace '[\r\n]+', ' '
    $text = $text -replace '\|', '/'
    $text = $text.Trim()
    if ($text.Length -gt 240) {
        $text = $text.Substring(0, 240)
    }
    if (Test-QnForbiddenMarker $text) {
        return '[redacted]'
    }
    return $text
}

function Assert-QnSafeReportPath {
    param(
        [string]$ResultsDirectory,
        [string]$SanitizedReportFileName
    )

    if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        throw 'ResultsDirectory is required when writing a sanitized protocol report.'
    }
    if ([string]::IsNullOrWhiteSpace($SanitizedReportFileName)) {
        throw 'SanitizedReportFileName is required when writing a sanitized protocol report.'
    }
    if ($SanitizedReportFileName -match '[\\/:\*\?"<>\|]' -or $SanitizedReportFileName -match '[\x00-\x1f]') {
        throw 'SanitizedReportFileName must be a single markdown file name without path separators or wildcards.'
    }
    if ($SanitizedReportFileName -eq '.' -or $SanitizedReportFileName -eq '..') {
        throw 'SanitizedReportFileName is not a valid file name.'
    }
    if (-not $SanitizedReportFileName.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SanitizedReportFileName must end with .md.'
    }
    if ([System.IO.Path]::GetFileName($SanitizedReportFileName) -ne $SanitizedReportFileName) {
        throw 'SanitizedReportFileName must not contain directory components.'
    }

    $resultsFull = Get-QnNormalizedFullPath $ResultsDirectory
    $live = Get-QnLiveProfileDirectory
    if ($resultsFull.Equals($live, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-QnPathIsUnder $resultsFull $live) -or
        (Test-QnPathIsUnder $live $resultsFull)) {
        throw 'ResultsDirectory must not be the live profile, inside it, or an ancestor of it.'
    }
    if ($resultsFull -match '(?i)qn-cloud-creds') {
        throw 'ResultsDirectory must not be the credentials directory.'
    }

    $dest = Get-QnNormalizedFullPath (Join-Path $resultsFull $SanitizedReportFileName)
    if (-not (Test-QnPathIsUnder $dest $resultsFull) -or $dest.Equals($resultsFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sanitized report path must stay inside the caller-provided results directory.'
    }
    return @{
        ResultsDirectory = $resultsFull
        ReportPath       = $dest
        TempPath         = $dest + '.tmp'
    }
}

function Get-QnUtcNowString {
    return [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

function Test-QnUtcTimestamp([string]$Value) {
    return $Value -match '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
}

function Test-QnRunId([string]$Value) {
    return $Value -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
}

function Assert-QnSafeCallerField([string]$Name, [string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) {
        return
    }
    if (Test-QnForbiddenMarker $Value) {
        throw "Refusing caller-supplied $Name that contains forbidden markers."
    }
}

function Assert-QnSafeArtifactFileName([string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }
    if ($Value -match '[\\/:\*\?"<>\|]' -or [System.IO.Path]::GetFileName($Value) -ne $Value) {
        throw "$Name must be a single file name without path separators or wildcards."
    }
}

function Get-QnProtocolTableValue([string]$Markdown, [string]$Field) {
    if ([string]::IsNullOrEmpty($Markdown) -or [string]::IsNullOrWhiteSpace($Field)) {
        return $null
    }
    $escaped = [regex]::Escape($Field)
    $match = [regex]::Match($Markdown, "(?m)^\s*\|\s*$escaped\s*\|\s*(.*?)\s*\|\s*$")
    if ($match.Success) {
        return $match.Groups[1].Value.Trim()
    }
    return $null
}

function Read-QnExistingProtocolIdentity([string]$ReportPath) {
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return $null
    }
    $text = [System.IO.File]::ReadAllText($ReportPath)
    return @{
        Schema     = Get-QnProtocolTableValue $text 'Schema'
        RunId      = Get-QnProtocolTableValue $text 'Run id'
        StartedUtc = Get-QnProtocolTableValue $text 'Started (UTC)'
        Scenario   = Get-QnProtocolTableValue $text 'Scenario'
    }
}

function Get-QnDotNetSdkVersion {
    try {
        $value = (& dotnet --version 2>$null | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($value)) {
            return '[unavailable]'
        }
        return $value.Trim()
    }
    catch {
        return '[unavailable]'
    }
}

function Get-QnAppVersion([string]$Root, [string]$Config) {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        return '[unavailable]'
    }
    $dll = Join-Path $Root "QuickNotes.App\bin\$Config\net8.0-windows10.0.19041.0\QuickNotes.App.dll"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        return '[unavailable]'
    }
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
        if (-not [string]::IsNullOrWhiteSpace($info.ProductVersion)) {
            return $info.ProductVersion.Trim()
        }
        if (-not [string]::IsNullOrWhiteSpace($info.FileVersion)) {
            return $info.FileVersion.Trim()
        }
        return '[unavailable]'
    }
    catch {
        return '[unavailable]'
    }
}

function Get-QnAwsSdkVersion([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        return '[unavailable]'
    }
    $csproj = Join-Path $Root 'QuickNotes.App\QuickNotes.App.csproj'
    if (-not (Test-Path -LiteralPath $csproj -PathType Leaf)) {
        return '[unavailable]'
    }
    $text = Get-Content -LiteralPath $csproj -Raw
    $match = [regex]::Match($text, 'Include="AWSSDK\.S3"\s+Version="([^"]+)"')
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return '[unavailable]'
}

function Get-QnCommitId([string]$Root) {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and $env:GITHUB_SHA -match '^[0-9a-fA-F]{7,40}$') {
        return $env:GITHUB_SHA.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($Root)) {
        return '[unavailable]'
    }
    try {
        $value = (& git -C $Root rev-parse HEAD 2>$null | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($value)) {
            return '[unavailable]'
        }
        $value = $value.Trim()
        if ($value -match '^[0-9a-fA-F]{7,40}$') {
            return $value
        }
        return '[unavailable]'
    }
    catch {
        return '[unavailable]'
    }
}

function Get-QnExpectedFilter([string]$ScenarioName) {
    if ($ScenarioName -eq 'Acceptance') {
        return 'FullyQualifiedName~YandexObjectStorageLiveAcceptanceTests.OptIn_TwoDevice_ProductionSync_KeepBothTombstoneAndProtectedScan'
    }
    return 'FullyQualifiedName~YandexObjectStorageLiveSmokeTests.OptIn_PutGetDelete_RoundTrip'
}

function Get-QnPrefixContract([string]$ScenarioName) {
    if ($ScenarioName -eq 'Acceptance') {
        return 'quicknotes-live-acceptance/<yyyyMMdd>/<guid>/'
    }
    return 'quicknotes-live-smoke/<yyyyMMdd>/<guid>/'
}

function Get-QnExpectedResult([string]$ScenarioName) {
    if ($ScenarioName -eq 'Acceptance') {
        return 'One filtered LiveCloud acceptance test passes under a unique code-defined prefix; live profile untouched; no claim of two Windows VMs or a dedicated bucket.'
    }
    return 'One filtered LiveCloud smoke PUT/GET/DELETE test passes under a unique code-defined prefix; live profile untouched.'
}

function Write-QnProtocolReportAtomic([string]$Destination, [string]$TempPath, [string]$Markdown) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $dir = [System.IO.Path]::GetDirectoryName($Destination)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    [System.IO.File]::WriteAllText($TempPath, $Markdown, $utf8)
    try {
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            $backup = $Destination + '.bak'
            [System.IO.File]::Replace($TempPath, $Destination, $backup)
            if (Test-Path -LiteralPath $backup -PathType Leaf) {
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            [System.IO.File]::Move($TempPath, $Destination)
        }
    }
    catch {
        if (Test-Path -LiteralPath $TempPath -PathType Leaf) {
            Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Build-QnProtocolMarkdown {
    param($Fields)

    $status = Get-QnSafeCell $Fields.State
    $outcome = Get-QnSafeCell $Fields.Outcome
    $limitations = 'Hard cancellation or runner loss can skip this file update and credential cleanup. Live object cleanup is owned by the filtered test under its unique prefix and is not evidenced here. This file does not prove a GitHub run, two Windows VMs, a dedicated bucket/IAM/lifecycle/spend limit, or live cleanup.'

    $lines = @(
        '# Cloud stand protocol (sanitized)',
        '',
        'Opt-in runner manifest for a later manual or GitHub cloud run. Values below are local reproducibility facts. This file does not claim that such a run already happened.',
        '',
        '| Field | Value |',
        '|---|---|',
        "| Schema | cloud-run-protocol/v1 |",
        "| Status | $status |",
        "| Outcome | $outcome |",
        "| Run id | $(Get-QnSafeCell $Fields.RunId) |",
        "| Started (UTC) | $(Get-QnSafeCell $Fields.StartedUtc) |",
        "| Updated (UTC) | $(Get-QnSafeCell $Fields.UpdatedUtc) |",
        "| Finished (UTC) | $(Get-QnSafeCell $Fields.FinishedUtc) |",
        "| Scenario | $(Get-QnSafeCell $Fields.Scenario) |",
        "| Expected filter | $(Get-QnSafeCell $Fields.ExpectedFilter) |",
        "| App version | $(Get-QnSafeCell $Fields.AppVersion) |",
        "| AWS SDK (AWSSDK.S3) | $(Get-QnSafeCell $Fields.AwsSdkVersion) |",
        "| .NET SDK | $(Get-QnSafeCell $Fields.DotNetSdkVersion) |",
        "| Runtime | $(Get-QnSafeCell $Fields.RuntimeVersion) |",
        "| OS | $(Get-QnSafeCell $Fields.OsDescription) |",
        "| Architecture | $(Get-QnSafeCell $Fields.Architecture) |",
        "| Endpoint host | $(Get-QnSafeCell $Fields.EndpointHost) |",
        "| Bucket fingerprint | $(Get-QnSafeCell $Fields.BucketFingerprint) |",
        "| Unique prefix (contract) | $(Get-QnSafeCell $Fields.PrefixContract) |",
        "| Test assembly | $(Get-QnSafeCell $Fields.TestAssembly) |",
        "| Commit | $(Get-QnSafeCell $Fields.CommitId) |",
        "| Exit code | $(Get-QnSafeCell $Fields.ExitCode) |",
        "| Duration (ms) | $(Get-QnSafeCell $Fields.DurationMs) |",
        "| Artifact filenames | $(Get-QnSafeCell $Fields.Artifacts) |",
        "| Cleanup attempted | $(Get-QnSafeCell $Fields.CleanupAttempted) |",
        "| Cleanup result | $(Get-QnSafeCell $Fields.CleanupResult) |",
        "| Actual result | $(Get-QnSafeCell $Fields.ActualResult) |",
        '',
        '## Expected result',
        '',
        (Get-QnSafeCell $Fields.ExpectedResult),
        '',
        '## Cleanup limitations',
        '',
        $limitations,
        ''
    )
    return ($lines -join "`n")
}

switch ($Action) {
    'Fingerprint' {
        Write-Output (Get-QnCloudBucketFingerprint $Bucket)
        exit 0
    }
    'SanitizeEndpoint' {
        Write-Output (Get-QnCloudEndpointHost $Endpoint)
        exit 0
    }
    'Write' {
        $paths = Assert-QnSafeReportPath -ResultsDirectory $ResultsDirectory -SanitizedReportFileName $SanitizedReportFileName
        Assert-QnSafeArtifactFileName -Name 'TrxLogFileName' -Value $TrxLogFileName
        foreach ($pair in @(
                @{ N = 'ActualResult'; V = $ActualResult },
                @{ N = 'ExpectedFilter'; V = $ExpectedFilter },
                @{ N = 'PrefixContract'; V = $PrefixContract },
                @{ N = 'AppVersion'; V = $AppVersion },
                @{ N = 'DotNetSdkVersion'; V = $DotNetSdkVersion },
                @{ N = 'RuntimeVersion'; V = $RuntimeVersion },
                @{ N = 'AwsSdkVersion'; V = $AwsSdkVersion },
                @{ N = 'OsDescription'; V = $OsDescription },
                @{ N = 'Architecture'; V = $Architecture },
                @{ N = 'TestAssembly'; V = $TestAssembly },
                @{ N = 'CommitId'; V = $CommitId },
                @{ N = 'CleanupAttempted'; V = $CleanupAttempted },
                @{ N = 'CleanupResult'; V = $CleanupResult },
                @{ N = 'ExitCode'; V = $ExitCode },
                @{ N = 'DurationMs'; V = $DurationMs },
                @{ N = 'Outcome'; V = $Outcome },
                @{ N = 'RunId'; V = $RunId },
                @{ N = 'StartedUtc'; V = $StartedUtc },
                @{ N = 'UpdatedUtc'; V = $UpdatedUtc },
                @{ N = 'FinishedUtc'; V = $FinishedUtc }
            )) {
            Assert-QnSafeCallerField -Name $pair.N -Value $pair.V
        }

        $scenarioName = if ([string]::IsNullOrWhiteSpace($Scenario)) { 'Smoke' } else { $Scenario }
        if (@('Smoke', 'Acceptance') -notcontains $scenarioName) {
            throw 'Scenario must be Smoke or Acceptance.'
        }
        $stateName = if ([string]::IsNullOrWhiteSpace($State)) { 'planned' } else { $State }
        if (@('planned', 'running', 'passed', 'failed', 'cleanup-attempted') -notcontains $stateName) {
            throw 'State must be planned, running, passed, failed, or cleanup-attempted.'
        }
        $outcomeName = $Outcome
        if ([string]::IsNullOrWhiteSpace($outcomeName)) {
            if ($stateName -eq 'passed') { $outcomeName = 'passed' }
            elseif ($stateName -eq 'failed') { $outcomeName = 'failed' }
            elseif ($stateName -eq 'cleanup-attempted' -and $ExitCode -eq '0') { $outcomeName = 'passed' }
            elseif ($stateName -eq 'cleanup-attempted') { $outcomeName = 'failed' }
            else { $outcomeName = 'unknown' }
        }
        if (@('unknown', 'passed', 'failed') -notcontains $outcomeName) {
            throw 'Outcome must be unknown, passed, or failed.'
        }
        if ($stateName -in @('planned', 'running') -and $outcomeName -ne 'unknown') {
            throw 'planned/running reports must use outcome unknown.'
        }
        if ($stateName -eq 'passed' -and $outcomeName -ne 'passed') {
            throw 'passed reports must use outcome passed.'
        }
        if ($stateName -eq 'failed' -and $outcomeName -ne 'failed') {
            throw 'failed reports must use outcome failed.'
        }

        $now = Get-QnUtcNowString
        $prior = Read-QnExistingProtocolIdentity $paths.ReportPath
        if ($null -ne $prior) {
            if ($prior.Schema -ne 'cloud-run-protocol/v1' -or [string]::IsNullOrWhiteSpace($prior.RunId) -or [string]::IsNullOrWhiteSpace($prior.StartedUtc)) {
                throw 'Refusing to replace an existing file that is not a cloud-run-protocol/v1 report with a stable identity.'
            }
            if (-not [string]::IsNullOrWhiteSpace($prior.Scenario) -and $prior.Scenario -ne $scenarioName) {
                throw 'Refusing to replace an existing protocol report with a different scenario.'
            }
        }

        $run = $RunId
        if (-not [string]::IsNullOrWhiteSpace($run) -and -not (Test-QnRunId $run)) {
            throw 'RunId must be a UUID.'
        }
        if ($prior) {
            if (-not [string]::IsNullOrWhiteSpace($run) -and $run.Trim() -ne $prior.RunId) {
                throw 'Refusing to replace an existing protocol report with a different run id.'
            }
            $run = $prior.RunId
        }
        elseif ([string]::IsNullOrWhiteSpace($run)) {
            $run = [guid]::NewGuid().ToString('D')
        }

        $started = $StartedUtc
        if (-not [string]::IsNullOrWhiteSpace($started) -and -not (Test-QnUtcTimestamp $started)) {
            throw 'StartedUtc must be an explicit UTC timestamp yyyy-MM-ddTHH:mm:ssZ.'
        }
        if ($prior) {
            if (-not [string]::IsNullOrWhiteSpace($started) -and $started.Trim() -ne $prior.StartedUtc) {
                throw 'Refusing to replace an existing protocol report with a different start time.'
            }
            $started = $prior.StartedUtc
        }
        elseif ([string]::IsNullOrWhiteSpace($started)) {
            $started = $now
        }

        $updated = if ([string]::IsNullOrWhiteSpace($UpdatedUtc)) { $now } else { $UpdatedUtc }
        if (-not (Test-QnUtcTimestamp $updated)) {
            throw 'UpdatedUtc must be an explicit UTC timestamp yyyy-MM-ddTHH:mm:ssZ.'
        }
        $finished = $FinishedUtc
        if ($stateName -in @('planned', 'running')) {
            $finished = ''
        }
        elseif ([string]::IsNullOrWhiteSpace($finished)) {
            $finished = $now
        }
        if (-not [string]::IsNullOrWhiteSpace($finished) -and -not (Test-QnUtcTimestamp $finished)) {
            throw 'FinishedUtc must be an explicit UTC timestamp yyyy-MM-ddTHH:mm:ssZ.'
        }

        $root = $RepoRoot
        $filter = if ([string]::IsNullOrWhiteSpace($ExpectedFilter)) { Get-QnExpectedFilter $scenarioName } else { $ExpectedFilter }
        $prefix = if ([string]::IsNullOrWhiteSpace($PrefixContract)) { Get-QnPrefixContract $scenarioName } else { $PrefixContract }
        $trxName = $TrxLogFileName
        $artifacts = @($SanitizedReportFileName)
        if (-not [string]::IsNullOrWhiteSpace($trxName)) {
            $artifacts = @($trxName, $SanitizedReportFileName)
        }

        $fields = [ordered]@{
            State             = $stateName
            Outcome           = $outcomeName
            RunId             = $run
            StartedUtc        = $started
            UpdatedUtc        = $updated
            FinishedUtc       = $finished
            Scenario          = $scenarioName
            ExpectedFilter    = $filter
            AppVersion        = $(if ([string]::IsNullOrWhiteSpace($AppVersion)) { Get-QnAppVersion $root $Configuration } else { $AppVersion })
            AwsSdkVersion     = $(if ([string]::IsNullOrWhiteSpace($AwsSdkVersion)) { Get-QnAwsSdkVersion $root } else { $AwsSdkVersion })
            DotNetSdkVersion  = $(if ([string]::IsNullOrWhiteSpace($DotNetSdkVersion)) { Get-QnDotNetSdkVersion } else { $DotNetSdkVersion })
            RuntimeVersion    = $(if ([string]::IsNullOrWhiteSpace($RuntimeVersion)) { [string][Environment]::Version } else { $RuntimeVersion })
            OsDescription     = $(if ([string]::IsNullOrWhiteSpace($OsDescription)) { [string][Environment]::OSVersion } else { $OsDescription })
            Architecture      = $(if ([string]::IsNullOrWhiteSpace($Architecture)) { [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE') } else { $Architecture })
            EndpointHost      = Get-QnCloudEndpointHost $Endpoint
            BucketFingerprint = Get-QnCloudBucketFingerprint $Bucket
            PrefixContract    = $prefix
            TestAssembly      = $(if ([string]::IsNullOrWhiteSpace($TestAssembly)) { 'QuickNotes.Tests' } else { $TestAssembly })
            CommitId          = $(if ([string]::IsNullOrWhiteSpace($CommitId)) { Get-QnCommitId $root } else { $CommitId })
            ExitCode          = $ExitCode
            DurationMs        = $DurationMs
            Artifacts         = ($artifacts -join ', ')
            CleanupAttempted  = $(if ([string]::IsNullOrWhiteSpace($CleanupAttempted)) { 'no' } else { $CleanupAttempted })
            CleanupResult     = $(if ([string]::IsNullOrWhiteSpace($CleanupResult)) { 'not-run' } else { $CleanupResult })
            ActualResult      = $(if ([string]::IsNullOrWhiteSpace($ActualResult)) { $stateName } else { $ActualResult })
            ExpectedResult    = Get-QnExpectedResult $scenarioName
        }

        $markdown = Build-QnProtocolMarkdown -Fields $fields
        $rawBucket = if ($null -eq $Bucket) { '' } else { $Bucket.Trim() }
        if ($rawBucket -and $markdown.Contains($rawBucket)) {
            throw 'Refusing to write a protocol report that would include the raw bucket name.'
        }
        $rawEndpoint = if ($null -eq $Endpoint) { '' } else { $Endpoint.Trim() }
        if ($rawEndpoint -and $markdown.Contains($rawEndpoint)) {
            throw 'Refusing to write a protocol report that would include the raw endpoint.'
        }
        if (Test-QnForbiddenMarker $markdown) {
            throw 'Refusing to write a protocol report that contains forbidden markers.'
        }

        Write-QnProtocolReportAtomic -Destination $paths.ReportPath -TempPath $paths.TempPath -Markdown $markdown
        Write-Output $paths.ReportPath
        exit 0
    }
}

exit 1
