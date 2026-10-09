<#
.SYNOPSIS
Opt-in physical scroll-FPS capture using a locally installed PresentMon.exe.

Validates an existing PresentMon path, launches QuickNotes on a unique isolated
profile, captures a bounded interval, and writes a Markdown report. Never uses
%LOCALAPPDATA%\QuickNotes as a profile and never writes it. Never downloads
tools or uses the network to fetch PresentMon. Never substitutes
CompositionTarget.Rendering timings for presentation FPS.

Manual action required during Capture: scroll the note list with the mouse
wheel or scrollbar for the full timed interval. The harness does not inject
destructive input and does not use cloud credentials.

.PARAMETER Mode
Capture (default) requires -PresentMonPath.
ParseOnly parses an existing CSV via QuickNotes.Tools.exe scroll-fps-parse.
GuardOnly writes an UNVERIFIED report without launching the UI or PresentMon.
ProbeUnsafeDelete proves live-profile recursive delete is refused.
#>
[CmdletBinding()]
param(
    [ValidateSet('Capture', 'ParseOnly', 'GuardOnly', 'ProbeUnsafeDelete')]
    [string]$Mode = 'Capture',
    [string]$RepoRoot,
    [string]$PresentMonPath,
    [int]$CaptureSeconds = 20,
    [string]$AppExe,
    [string]$IsolatedProfile,
    [string]$ArtifactsDirectory,
    [string]$RunId,
    [string]$CsvPath,
    [string]$ToolsExe,
    [switch]$OperatorConfirmsScroll,
    [switch]$SmokeDataset,
    [switch]$KeepProfile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell -File leaves $PSScriptRoot empty during param() defaults.
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and -not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
        $scriptDir = Split-Path -Parent $PSCommandPath
    }
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        throw 'Unable to resolve script directory for default RepoRoot. Pass -RepoRoot explicitly.'
    }
    $RepoRoot = Split-Path -Parent $scriptDir
}

function Get-NormalizedFullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathIsUnder([string]$Candidate, [string]$Ancestor) {
    $c = (Get-NormalizedFullPath $Candidate).TrimEnd('\', '/')
    $a = (Get-NormalizedFullPath $Ancestor).TrimEnd('\', '/')
    if ($c.Equals($a, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $sep = [IO.Path]::DirectorySeparatorChar
    $alt = [IO.Path]::AltDirectorySeparatorChar
    return $c.StartsWith($a + $sep, [StringComparison]::OrdinalIgnoreCase) -or
        $c.StartsWith($a + $alt, [StringComparison]::OrdinalIgnoreCase)
}

function Get-LiveProfileDirectory {
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($local)) {
        $local = $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($local)) {
        throw 'LOCALAPPDATA is not available; refusing to continue.'
    }
    return Get-NormalizedFullPath (Join-Path $local 'QuickNotes')
}

function Assert-NotLiveProfileRelated([string]$Path, [string]$Role) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Role path cannot be empty."
    }
    $full = Get-NormalizedFullPath $Path
    $live = Get-LiveProfileDirectory
    if ($full.Equals($live, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must not be the live profile: $full"
    }
    if (Test-PathIsUnder $full $live) {
        throw "$Role must not be inside the live profile: $full"
    }
    if (Test-PathIsUnder $live $full) {
        throw "$Role must not be an ancestor of the live profile: $full"
    }
}

function Assert-UniqueOwnedPath([string]$Path, [string]$OwnedRoot, [string]$Role) {
    Assert-NotLiveProfileRelated $Path $Role
    $full = Get-NormalizedFullPath $Path
    $root = Get-NormalizedFullPath $OwnedRoot
    if (-not (Test-PathIsUnder $full $root)) {
        throw "$Role must stay under uniquely owned root $root (got $full)."
    }
}

function Remove-OwnedDirectory([string]$Path, [string]$OwnedRoot) {
    $full = Get-NormalizedFullPath $Path
    $root = Get-NormalizedFullPath $OwnedRoot
    Assert-NotLiveProfileRelated $full 'Cleanup target'
    if (-not (Test-PathIsUnder $full $root)) {
        throw "Refusing recursive delete outside uniquely owned directory: $full (owned $root)"
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function Get-MissingPresentMonMessage {
    return 'PresentMon is not installed at the given path. Install Intel PresentMon locally and pass -PresentMonPath to an existing PresentMon*.exe. This harness will not download tools, will not use the network, and will not substitute CompositionTarget.Rendering timings for presentation FPS.'
}

function Assert-PresentMonPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw (Get-MissingPresentMonMessage)
    }
    if ($Path -match '://' -or $Path -match '(?i)^https?') {
        throw 'PresentMon path must be a local executable that is already installed. Download URLs are refused.'
    }
    $full = Get-NormalizedFullPath $Path
    if (-not (Test-Path -LiteralPath $full)) {
        throw ((Get-MissingPresentMonMessage) + " Missing file: $full")
    }
    $name = Split-Path -Leaf $full
    if ($name -notmatch '(?i)PresentMon.*\.exe$') {
        throw "Expected a local PresentMon*.exe. Refusing '$name'. $(Get-MissingPresentMonMessage)"
    }
    return $full
}

function Get-PresentMonHelp([string]$Exe) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '--help'
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(10000)) {
        try { $proc.Kill($true) } catch { }
        throw "PresentMon help timed out: $Exe"
    }
    return ($proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd())
}

function Get-PresentMonVersion([string]$Exe) {
    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
        if (-not [string]::IsNullOrWhiteSpace($info.FileVersion)) { return $info.FileVersion }
        if (-not [string]::IsNullOrWhiteSpace($info.ProductVersion)) { return $info.ProductVersion }
    } catch { }
    return 'unknown'
}

function Clear-CloudEnvironment {
    $names = @(
        'QUICKNOTES_LIVE_S3_REQUIRED',
        'QUICKNOTES_LIVE_S3_ENDPOINT',
        'QUICKNOTES_LIVE_S3_BUCKET',
        'QUICKNOTES_LIVE_S3_ACCESS_KEY',
        'QUICKNOTES_LIVE_S3_SECRET_KEY',
        'AWS_ACCESS_KEY_ID',
        'AWS_SECRET_ACCESS_KEY',
        'AWS_SESSION_TOKEN'
    )
    foreach ($name in $names) {
        Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
    }
}

function Write-UnverifiedStub([string]$ReportPath, [string]$Reason, [string]$Csv) {
    $os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    $md = @(
        '# QuickNotes physical scroll FPS (PresentMon)',
        '',
        'Opt-in ROADMAP §6.7 / §6.5 list-scroll gate. `CompositionTarget.Rendering` is not used as FPS proof.',
        '',
        '| Field | Value |',
        '|---|---|',
        '| **Result** | **UNVERIFIED** |',
        "| Tool | PresentMon |",
        '| Tool version | n/a |',
        "| OS | $os |",
        '| Display | not captured |',
        "| Raw CSV | ``$Csv`` |",
        '',
        '## Manual action required during capture',
        '',
        'While PresentMon is recording, **scroll the note list** with the mouse wheel or scrollbar for the full capture interval. The harness does not inject scroll, key, or click input and does not touch cloud credentials.',
        '',
        '## Reason',
        '',
        $Reason,
        ''
    ) -join [Environment]::NewLine
    $dir = Split-Path -Parent $ReportPath
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $ReportPath -Value $md -Encoding utf8
}

$repo = Get-NormalizedFullPath $RepoRoot
Set-Location $repo
Clear-CloudEnvironment

if ($CaptureSeconds -lt 5 -or $CaptureSeconds -gt 120) {
    throw 'CaptureSeconds must be between 5 and 120 (bounded interval).'
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '_' + ([guid]::NewGuid().ToString('N').Substring(0, 12))
}

$artifactsRoot = Join-Path $repo 'artifacts\scroll-fps'
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $artifactsRoot $RunId
}
$ArtifactsDirectory = Get-NormalizedFullPath $ArtifactsDirectory
Assert-NotLiveProfileRelated $ArtifactsDirectory 'Artifacts directory'
if (-not (Test-PathIsUnder $ArtifactsDirectory $artifactsRoot) -and -not (Test-PathIsUnder $ArtifactsDirectory ([IO.Path]::GetTempPath()))) {
    throw "Artifacts directory must be under artifacts/scroll-fps or temp: $ArtifactsDirectory"
}
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null

$ownedTempRoot = Join-Path ([IO.Path]::GetTempPath()) ("qn-scroll-fps-" + $RunId)
if ([string]::IsNullOrWhiteSpace($IsolatedProfile)) {
    $IsolatedProfile = Join-Path $ownedTempRoot 'profile'
}
$IsolatedProfile = Get-NormalizedFullPath $IsolatedProfile
Assert-NotLiveProfileRelated $IsolatedProfile 'Isolated profile'

$reportPath = Join-Path $ArtifactsDirectory 'scroll-fps-presentmon.md'
$csvOut = Join-Path $ArtifactsDirectory 'presentmon.csv'

if ($Mode -eq 'ProbeUnsafeDelete') {
    $live = Get-LiveProfileDirectory
    try {
        Remove-OwnedDirectory -Path $live -OwnedRoot $ownedTempRoot
        throw 'Expected live profile delete to be refused.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'live profile|Refusing recursive delete|ancestor') {
            throw
        }
        Write-Host 'refused unsafe recursive delete'
        exit 0
    }
}

if ($Mode -eq 'GuardOnly') {
    New-Item -ItemType Directory -Path $IsolatedProfile -Force | Out-Null
    Assert-UniqueOwnedPath $IsolatedProfile $ownedTempRoot 'Isolated profile'
    Write-UnverifiedStub -ReportPath $reportPath -Csv '(none)' -Reason 'GuardOnly: no PresentMon capture was performed. ROADMAP scroll FPS remains open.'
    Write-Host "UNVERIFIED report: $reportPath"
    Write-Host "Isolated profile: $IsolatedProfile"
    if (-not $KeepProfile) {
        Remove-OwnedDirectory -Path $ownedTempRoot -OwnedRoot $ownedTempRoot
    }
    exit 0
}

if ($Mode -eq 'ParseOnly') {
    if ([string]::IsNullOrWhiteSpace($CsvPath) -or -not (Test-Path -LiteralPath $CsvPath)) {
        throw 'ParseOnly requires -CsvPath pointing to an existing PresentMon CSV or log.'
    }
    if ([string]::IsNullOrWhiteSpace($ToolsExe)) {
        $ToolsExe = Join-Path $repo 'QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe'
    }
    if (-not (Test-Path -LiteralPath $ToolsExe)) {
        throw "Missing Tools exe for ParseOnly: $ToolsExe. Build Release first (dotnet build -c Release)."
    }
    $toolArgs = @(
        'scroll-fps-parse',
        '--csv', (Get-NormalizedFullPath $CsvPath),
        '--report', $reportPath
    )
    if ($OperatorConfirmsScroll) {
        $toolArgs += '--operator-confirms-scroll'
    }
    & $ToolsExe @toolArgs
    $code = $LASTEXITCODE
    Write-Host "ParseOnly finished exit $code report $reportPath"
    exit $code
}

# Capture
$presentMon = Assert-PresentMonPath $PresentMonPath
$pmVersion = Get-PresentMonVersion $presentMon
$help = Get-PresentMonHelp $presentMon
$useLong = $help -match '--timed' -or $help -match '--process_id'
Write-Host "PresentMon $pmVersion at $presentMon"

if ([string]::IsNullOrWhiteSpace($AppExe)) {
    $AppExe = Join-Path $repo 'QuickNotes.App\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.App.exe'
}
$AppExe = Get-NormalizedFullPath $AppExe
if (-not (Test-Path -LiteralPath $AppExe)) {
    throw "Missing QuickNotes.App.exe: $AppExe. Build Release first."
}

New-Item -ItemType Directory -Path $IsolatedProfile -Force | Out-Null
Assert-UniqueOwnedPath $IsolatedProfile $ownedTempRoot 'Isolated profile'

if ([string]::IsNullOrWhiteSpace($ToolsExe)) {
    $ToolsExe = Join-Path $repo 'QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe'
}

$app = $null
$pm = $null
try {
    Write-Host 'Launching isolated QuickNotes profile (not perf-scroll; no CompositionTarget FPS).'
    Write-Host 'MANUAL ACTION: scroll the note list with the mouse wheel or scrollbar until capture ends.'
    $appPsi = New-Object System.Diagnostics.ProcessStartInfo
    $appPsi.FileName = $AppExe
    $appPsi.ArgumentList.Add('--isolated-profile')
    $appPsi.ArgumentList.Add($IsolatedProfile)
    $appPsi.UseShellExecute = $false
    $app = [Diagnostics.Process]::Start($appPsi)
    if ($null -eq $app) {
        throw 'Failed to start QuickNotes.App.exe'
    }

    Start-Sleep -Seconds 3

    $pmPsi = New-Object System.Diagnostics.ProcessStartInfo
    $pmPsi.FileName = $presentMon
    $pmPsi.UseShellExecute = $false
    $pmPsi.RedirectStandardOutput = $true
    $pmPsi.RedirectStandardError = $true
    $pmPsi.CreateNoWindow = $true
    if ($useLong) {
        $pmPsi.ArgumentList.Add('--process_id')
        $pmPsi.ArgumentList.Add("$($app.Id)")
        $pmPsi.ArgumentList.Add('--output_file')
        $pmPsi.ArgumentList.Add($csvOut)
        $pmPsi.ArgumentList.Add('--timed')
        $pmPsi.ArgumentList.Add("$CaptureSeconds")
        $pmPsi.ArgumentList.Add('--terminate_after_timed')
        $pmPsi.ArgumentList.Add('--stop_existing_session')
    }
    else {
        $pmPsi.ArgumentList.Add('-process_id')
        $pmPsi.ArgumentList.Add("$($app.Id)")
        $pmPsi.ArgumentList.Add('-output_file')
        $pmPsi.ArgumentList.Add($csvOut)
        $pmPsi.ArgumentList.Add('-timed')
        $pmPsi.ArgumentList.Add("$CaptureSeconds")
        $pmPsi.ArgumentList.Add('-terminate_after_timed')
        $pmPsi.ArgumentList.Add('-stop_existing_session')
    }
    $pm = [Diagnostics.Process]::Start($pmPsi)
    if ($null -eq $pm) {
        throw 'Failed to start PresentMon'
    }

    $waitMs = ($CaptureSeconds + 15) * 1000
    if (-not $pm.WaitForExit($waitMs)) {
        try { $pm.Kill($true) } catch { }
        throw "PresentMon exceeded bounded capture interval ($CaptureSeconds s)."
    }

    if (-not (Test-Path -LiteralPath $csvOut)) {
        $stderr = $pm.StandardError.ReadToEnd()
        $stdout = $pm.StandardOutput.ReadToEnd()
        Set-Content -LiteralPath $csvOut -Value ($stdout + $stderr) -Encoding utf8
    }

    if (Test-Path -LiteralPath $ToolsExe) {
        $parseArgs = @(
            'scroll-fps-parse',
            '--csv', $csvOut,
            '--report', $reportPath,
            '--tool-name', 'PresentMon',
            '--tool-version', $pmVersion
        )
        if ($OperatorConfirmsScroll) {
            $parseArgs += '--operator-confirms-scroll'
        }
        & $ToolsExe @parseArgs
        $code = $LASTEXITCODE
    }
    else {
        Write-UnverifiedStub -ReportPath $reportPath -Csv $csvOut -Reason 'QuickNotes.Tools.exe missing; CSV captured but not parsed. Build Release Tools.'
        $code = 2
    }

    Write-Host "Capture finished exit $code report $reportPath csv $csvOut"
    exit $code
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        try { $app.Kill($true) } catch { }
    }
    if ($null -ne $pm -and -not $pm.HasExited) {
        try { $pm.Kill($true) } catch { }
    }
    if (-not $KeepProfile -and (Test-Path -LiteralPath $ownedTempRoot)) {
        Remove-OwnedDirectory -Path $ownedTempRoot -OwnedRoot $ownedTempRoot
    }
}
