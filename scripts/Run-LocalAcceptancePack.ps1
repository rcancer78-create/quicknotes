<#
.SYNOPSIS
Consolidated local acceptance entry point for ROADMAP §6.6 / M5 product checks.

Creates a unique isolated profile and artifact directory. Never uses
%LOCALAPPDATA%\QuickNotes as a profile and never writes it. A metadata-only
snapshot (relative path, length, LastWriteTimeUtc ticks) is read before/after
to detect mutation; this is not a cryptographic content hash.

Does not use cloud credentials (clears QUICKNOTES_LIVE_S3_* plus AWS key,
profile, region, and shared-credentials env vars in this process only, so the
dotnet test child inherits the sanitized environment; excludes LiveCloud tests).
Does not claim the calling shell or other host processes are sanitized. The
test step is --no-restore --no-build and therefore does not contact NuGet
feeds; a prior local `dotnet build` is required. Does not claim the host has
no other network activity. A valid TRX with zero executed tests is a failure.

Does not claim physical DPI, real-app capture, or live Sync UI as passed.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [ValidateSet('GuardOnly', 'SelfTestFailure', 'SelfTestZeroExecuted', 'ProbeUnsafeDelete', 'Lightweight', 'Focused')]
    [string]$Mode = 'Focused',
    [string]$Configuration = 'Release',
    [string]$RunId,
    [string]$IsolatedProfile,
    [string]$ArtifactsDirectory
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
    $leaf = Split-Path -Leaf $root
    if ($leaf -notmatch '^qn-local-acceptance-[0-9A-Fa-f]{8,}' -and $leaf -notmatch '^[0-9]{8}T[0-9]{6}Z_[0-9a-f]{12}$') {
        throw "Owned root leaf is not a unique local-acceptance id: $root"
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

function Get-LiveProfileSnapshot {
    # Metadata-only mutation probe: names, lengths, LastWriteTimeUtc. Not a content hash.
    # Never opens note/DB bytes. Never writes. Live directory is not used as the app profile.
    $live = Get-LiveProfileDirectory
    if (-not (Test-Path -LiteralPath $live)) {
        return 'ABSENT'
    }
    $lines = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -LiteralPath $live -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring((Get-NormalizedFullPath $live).Length).TrimStart('\', '/').Replace('\', '/')
        if ($_.PSIsContainer) {
            [void]$lines.Add("D|$relative")
        }
        else {
            [void]$lines.Add("F|$relative|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)")
        }
    }
    return [string]::Join("`n", $lines)
}

function Assert-LocalBuildPrecondition([string]$Root, [string]$Config) {
    $tfm = 'net8.0-windows10.0.19041.0'
    $testDll = Join-Path $Root "QuickNotes.Tests\bin\$Config\$tfm\QuickNotes.Tests.dll"
    $appDll = Join-Path $Root "QuickNotes.App\bin\$Config\$tfm\QuickNotes.App.dll"
    $missing = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $testDll)) { [void]$missing.Add($testDll) }
    if (-not (Test-Path -LiteralPath $appDll)) { [void]$missing.Add($appDll) }
    if ($missing.Count -gt 0) {
        throw ("Local $Config build outputs are missing; refusing to restore or hit package feeds. " +
            "Run: dotnet build `"$(Join-Path $Root 'QuickNotes.sln')`" -c $Config`nMissing:`n - " +
            ($missing -join "`n - "))
    }
}

function Clear-LiveCloudEnvironment {
    $cleared = New-Object System.Collections.Generic.List[string]
    foreach ($name in @(
            'QUICKNOTES_LIVE_S3_REQUIRED',
            'QUICKNOTES_LIVE_S3_CREDENTIALS_PATH',
            'QUICKNOTES_LIVE_S3_ENDPOINT',
            'QUICKNOTES_LIVE_S3_BUCKET',
            'AWS_ACCESS_KEY_ID',
            'AWS_SECRET_ACCESS_KEY',
            'AWS_SESSION_TOKEN',
            'AWS_PROFILE',
            'AWS_REGION',
            'AWS_DEFAULT_REGION',
            'AWS_SHARED_CREDENTIALS_FILE')) {
        if (Test-Path -Path "Env:$name") {
            [void]$cleared.Add($name)
            Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
        }
    }
    return $cleared
}

function Read-AcceptanceTrxCounters([string]$TrxPath) {
    if (-not (Test-Path -LiteralPath $TrxPath)) {
        throw "TRX was not written: $TrxPath"
    }

    [xml]$doc = Get-Content -LiteralPath $TrxPath -Raw
    $counters = $doc.SelectSingleNode('//*[local-name()="Counters"]')
    if ($null -eq $counters) {
        throw "TRX is missing ResultSummary.Counters: $TrxPath"
    }

    $total = [int]$counters.GetAttribute('total')
    $executedAttr = $counters.GetAttribute('executed')
    $executed = if ([string]::IsNullOrWhiteSpace($executedAttr)) { $total } else { [int]$executedAttr }
    $passed = [int]$counters.GetAttribute('passed')
    $failed = [int]$counters.GetAttribute('failed')
    $skippedAttr = $counters.GetAttribute('skipped')
    $skipped = if ([string]::IsNullOrWhiteSpace($skippedAttr)) { 0 } else { [int]$skippedAttr }

    return [pscustomobject]@{
        Total    = $total
        Executed = $executed
        Passed   = $passed
        Failed   = $failed
        Skipped  = $skipped
    }
}

function Assert-AcceptanceTrxHasExecutedTests($Counters) {
    if ($null -eq $Counters -or [int]$Counters.Executed -le 0) {
        $total = if ($null -eq $Counters) { 'n/a' } else { $Counters.Total }
        $executed = if ($null -eq $Counters) { 'n/a' } else { $Counters.Executed }
        throw "Valid TRX reported zero executed tests (total=$total, executed=$executed)."
    }
}

function Write-ZeroExecutedAcceptanceTrx([string]$TrxPath) {
    $xml = @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" name="zero-executed" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="0" executed="0" passed="0" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
'@
    $dir = Split-Path -Parent $TrxPath
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    [System.IO.File]::WriteAllText($TrxPath, $xml)
}

function Get-FocusedClassFilter {
    $classes = @(
        'ThreePaneWorkspaceSmokeTests',
        'ThemeAndPaletteGuardTests',
        'UiSettingsAndSortingTests',
        'SourceContextAndHotkeyTests',
        'PortabilityAndDailyTests',
        'DraftJournalTests',
        'DraftJournalIsolationRegressionTests',
        'ReliabilityAndSearchPreviewTests',
        'NoteTemplateTests',
        'TagRuleTests',
        'TagTreeImprovementTests',
        'TagRescanPreviewTests',
        'SearchVerticalSliceTests',
        'SmartSearchTests',
        'NoteAttachmentViewModelTests',
        'AttachmentStorageServiceTests',
        'ScreenOcrTests',
        'NoteHistoryTests',
        'ImportExportTests',
        'OpenExportTests',
        'ImportMigrationSmokeTests',
        'ImportMigrationUiTests',
        'NewFeatureTests',
        'AuditAndReliabilityTests',
        'SyncSettingsViewModelTests',
        'SyncConflictsUiTests',
        'SyncConflictSmokeTests',
        'SyncConflictsViewModelTests',
        'SyncXamlGuardTests',
        'CloudUsageServiceTests',
        'CloudRetentionAndSmokeTests',
        'CloudPasswordRotationTests',
        'MainViewModelSyncTests',
        'RegressionTests'
    )
    $parts = foreach ($c in $classes) { "FullyQualifiedName~$c" }
    return '(' + ($parts -join '|') + ')&FullyQualifiedName!~YandexObjectStorage&FullyQualifiedName!~LocalAcceptancePackTests&Category!=LiveCloud'
}

function Get-TestFilter([string]$RunMode) {
    $base = Get-FocusedClassFilter
    if ($RunMode -eq 'Lightweight') {
        return $base + '&FullyQualifiedName!~GenerateAcceptanceScreenshots&FullyQualifiedName!~RunIsolatedUiSmokeProcess'
    }
    return $base
}

function Get-CoverageMatrixMarkdown {
    return @'
| ROADMAP §5.6 / M5 line | Automated evidence (synthetic/offline) | Status | Manual-only remainder |
|---|---|---|---|
| Main window: ordinary/narrow width, light/dark, 100/125/150/200% DPI | `ThreePaneWorkspaceSmokeTests` (wide/narrow layout, synthetic DPI layout inspect), `ThemeAndPaletteGuardTests`, `UiSettingsAndSortingTests` | Partial | Physical OS DPI scaling on a real monitor; visual judgement of clipping/contrast |
| Quick capture: different apps, Unicode, empty/unavailable clipboard, repeat hotkey | `SourceContextAndHotkeyTests`, `PortabilityAndDailyTests.ClipboardCaptureService_DocumentsUserFacingFailureReasons`, `RegressionTests.Hotkey_*` | Partial | Capture from real foreground apps; truly empty/locked clipboard; in-flight repeat hotkey with a user |
| Editor: create, cancel, explicit save, crash recovery, long note | `DraftJournalTests` (commit/cancel/crash journal), `ReliabilityAndSearchPreviewTests.NoteEditor_HasUnsavedChangesAfterEdit`, `NoteAttachmentViewModelTests.NoteEditorViewModel_Cancel_*`, `NoteTemplateTests` (50k-char body bound) | Partial | Kill the real UI process and recover via `DraftRecoveryWindow`; author a long note by hand |
| Tags: tree, synonyms, word boundaries, manual/auto/suppressed | `TagRuleTests`, `TagTreeImprovementTests`, `TagRescanPreviewTests` | Partial | Visual tree editing and chip states in the running app |
| Search: text, tags, FTS fallback, protected notes, empty result | `SearchVerticalSliceTests`, `SmartSearchTests`, `ReliabilityAndSearchPreviewTests.SearchPreview_*` | Partial | Type-in-UI empty result and protected-note hiding on a live window |
| Attachments, OCR, history, import/export, trash | `NoteAttachmentViewModelTests`, `AttachmentStorageServiceTests`, `ScreenOcrTests`, `NoteHistoryTests`, `ImportExportTests`, `OpenExportTests`, `ImportMigrationUiTests` / smoke, `ReliabilityAndSearchPreviewTests.PurgeOldTrash_*`, `AuditAndReliabilityTests.PurgeOldTrash_*`, `NewFeatureTests.SearchSections_ExcludeTrash*` | Partial | File-picker attachments, real screen OCR crop, history dialog, import/export wizard, trash UI restore |
| Sync UI: setup, status, conflict, quota, cleanup/rotation | `SyncSettingsViewModelTests`, `SyncConflictsUiTests` / `SyncConflictSmokeTests`, `CloudUsageServiceTests`, `CloudRetentionAndSmokeTests`, `CloudPasswordRotationTests`, `MainViewModelSyncTests` (fake store) | Partial | Configure a real test bucket, live quota/status, conflict window after real two-device edit |
| Primary hotkey flow without regression | `SourceContextAndHotkeyTests.SaveInstantNote_*`, `RegressionTests.Hotkey_ReplacementConflictAndDispose_PreserveRegistration` | Partial | End-to-end hotkey from another app to inbox note |
'@
}

function Get-ManualChecklistMarkdown {
    return @'
These items are **not passed** by this runner. Synthetic tests must not be recorded as physical/user validation.

1. Resize the real main window to ordinary and narrow widths; switch light and dark themes; set Windows display scaling to 100%, 125%, 150%, and 200% on a physical monitor. Expected: panes remain usable, no clipped toolbar/status, theme contrast holds.
2. Capture from at least two other applications (for example a browser and a text editor), including Unicode text; try with an empty clipboard and with the clipboard held by another process; press the capture hotkey again while a capture is in progress. Expected: note text matches the source or a user-facing failure reason; no hang; no live-profile corruption.
3. In the editor: create a note, cancel without saving, save explicitly, kill the process with an unsaved draft and restore/discard via the recovery dialog, and edit a multi-thousand-line note. Expected: cancel leaves no committed note; save persists; recovery matches the journal; UI stays responsive.
4. Exercise the tag tree, synonyms, word-boundary rules, and manual vs automatic vs suppressed chips in the UI. Expected: counters/paths update; suppressed auto-tags stay off; no silent retag of unrelated notes.
5. Search in the running app: free text, tag filters, a query with no hits, and a protected note. Expected: hits match title/body/tags; empty state is obvious; protected plaintext is not shown while locked.
6. Attach a real file, run screen OCR on a visible window, restore a history revision, import and export through the wizard, move a note to trash and restore it. Expected: files round-trip; OCR opens a draft without auto-commit; history restore writes a new revision; trash restore returns the note.
7. Open Sync settings, save test (not production) credentials, watch status, open a real conflict, read quota, and trigger cleanup/rotation. Expected: secrets stay masked; conflict actions are explicit; quota/cleanup do not touch objects outside the test prefix. Do not use the live user bucket for this pack.
8. From another app, use the primary global hotkey through capture to a saved inbox note and confirm the existing mapping still works.
'@
}

$repo = Get-NormalizedFullPath $RepoRoot
if (-not (Test-Path -LiteralPath (Join-Path $repo 'QuickNotes.sln'))) {
    throw "RepoRoot does not look like QuickNotes: $repo"
}

$startedUtc = [DateTime]::UtcNow
if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = $startedUtc.ToString('yyyyMMddTHHmmssZ') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
}

$tempRoot = Get-NormalizedFullPath (Join-Path ([IO.Path]::GetTempPath()) ("qn-local-acceptance-" + $RunId))
$defaultProfile = Join-Path $tempRoot 'profile'
$defaultArtifacts = Join-Path $repo (Join-Path 'artifacts\local-acceptance' $RunId)

if ([string]::IsNullOrWhiteSpace($IsolatedProfile)) {
    $IsolatedProfile = $defaultProfile
}
else {
    $IsolatedProfile = Get-NormalizedFullPath $IsolatedProfile
}

if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = $defaultArtifacts
}
else {
    $ArtifactsDirectory = Get-NormalizedFullPath $ArtifactsDirectory
}

Assert-UniqueOwnedPath $IsolatedProfile $tempRoot 'Isolated profile'
$artifactsOwnedRoot = Get-NormalizedFullPath (Join-Path $repo 'artifacts\local-acceptance')
Assert-NotLiveProfileRelated $ArtifactsDirectory 'Artifacts directory'
if (-not (Test-PathIsUnder $ArtifactsDirectory $artifactsOwnedRoot)) {
    throw "Artifacts directory must stay under $artifactsOwnedRoot (got $ArtifactsDirectory)."
}
$artifactLeaf = Split-Path -Leaf $ArtifactsDirectory
if ($artifactLeaf -ne $RunId) {
    throw "Artifacts leaf must equal RunId '$RunId' (got '$artifactLeaf')."
}

if ($Mode -eq 'ProbeUnsafeDelete') {
    $live = Get-LiveProfileDirectory
    try {
        Remove-OwnedDirectory -Path $live -OwnedRoot $tempRoot
        throw 'ProbeUnsafeDelete expected a refusal, but delete was not blocked.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Refusing recursive delete|must not be the live profile|must not be an ancestor|must not be inside') {
            throw
        }
        Write-Output "ProbeUnsafeDelete refused unsafe recursive delete: $($_.Exception.Message)"
        exit 0
    }
}

$clearedEnv = @(Clear-LiveCloudEnvironment)
$liveBefore = Get-LiveProfileSnapshot

New-Item -ItemType Directory -Force -Path $IsolatedProfile | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsDirectory | Out-Null
Assert-UniqueOwnedPath $IsolatedProfile $tempRoot 'Isolated profile'

$dotnetVersion = 'UNKNOWN'
try { $dotnetVersion = (dotnet --version).Trim() } catch { }

$gitHead = 'UNKNOWN'
try { $gitHead = (git -C $repo rev-parse HEAD).Trim() } catch { }

$osDesc = [Environment]::OSVersion.VersionString
$machine = $env:COMPUTERNAME
$manifestPath = Join-Path $ArtifactsDirectory 'local-acceptance-manifest.md'
$trxDir = Join-Path $ArtifactsDirectory 'trx'
New-Item -ItemType Directory -Force -Path $trxDir | Out-Null

$commands = New-Object System.Collections.Generic.List[string]
$exitCode = 0
$passed = 0
$failed = 0
$total = 0
$skipped = 0
$filterUsed = '(none)'
$testCommand = '(not run)'

try {
    if ($Mode -eq 'SelfTestFailure') {
        $failed = 1
        $total = 1
        $exitCode = 1
        $testCommand = 'self-test injected failure (no cloud credentials; isolated profile only)'
        [void]$commands.Add($testCommand)
    }
    elseif ($Mode -eq 'GuardOnly') {
        $testCommand = 'guard-only (paths created, tests not executed)'
        [void]$commands.Add($testCommand)
        $total = 0
        $passed = 0
    }
    elseif ($Mode -eq 'SelfTestZeroExecuted') {
        $trx = Join-Path $trxDir 'local-acceptance.trx'
        Write-ZeroExecutedAcceptanceTrx $trx
        $testCommand = 'self-test valid TRX with zero executed tests'
        [void]$commands.Add($testCommand)
        $parsed = Read-AcceptanceTrxCounters $trx
        $total = [int]$parsed.Total
        $passed = [int]$parsed.Passed
        $failed = [int]$parsed.Failed
        $skipped = [int]$parsed.Skipped
        Assert-AcceptanceTrxHasExecutedTests $parsed
    }
    else {
        Assert-LocalBuildPrecondition -Root $repo -Config $Configuration
        $filterUsed = Get-TestFilter $Mode
        $testCommand = "dotnet test `"$(Join-Path $repo 'QuickNotes.Tests\QuickNotes.Tests.csproj')`" -c $Configuration --no-restore --no-build --nologo --blame-hang --blame-hang-timeout 2m --filter `"$filterUsed`" --results-directory `"$trxDir`" --logger `"trx;LogFileName=local-acceptance.trx`""
        [void]$commands.Add($testCommand)
        $env:QUICKNOTES_LOCAL_ACCEPTANCE_RUN = '1'
        try {
            & dotnet test (Join-Path $repo 'QuickNotes.Tests\QuickNotes.Tests.csproj') `
                -c $Configuration `
                --no-restore `
                --no-build `
                --nologo `
                --blame-hang `
                --blame-hang-timeout 2m `
                --filter "$filterUsed" `
                --results-directory "$trxDir" `
                --logger 'trx;LogFileName=local-acceptance.trx'
            $exitCode = $LASTEXITCODE
        }
        finally {
            Remove-Item Env:QUICKNOTES_LOCAL_ACCEPTANCE_RUN -ErrorAction SilentlyContinue
        }
        if ($null -eq $exitCode) { $exitCode = 1 }

        $trx = Join-Path $trxDir 'local-acceptance.trx'
        if (Test-Path -LiteralPath $trx) {
            $parsed = Read-AcceptanceTrxCounters $trx
            $total = [int]$parsed.Total
            $passed = [int]$parsed.Passed
            $failed = [int]$parsed.Failed
            $skipped = [int]$parsed.Skipped
            Assert-AcceptanceTrxHasExecutedTests $parsed
        }
        elseif ($exitCode -eq 0) {
            throw 'dotnet test reported success but TRX was not written.'
        }
    }
}
catch {
    $exitCode = 1
    [void]$commands.Add('ERROR: ' + $_.Exception.Message)
    Write-Warning $_.Exception.Message
}
finally {
    $liveAfter = Get-LiveProfileSnapshot
    $liveUnchanged = ($liveBefore -eq $liveAfter)
    if (-not $liveUnchanged) {
        $exitCode = 1
    }

    $finishedUtc = [DateTime]::UtcNow
    $status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
    $clearedText = if (@($clearedEnv).Count -eq 0) { '(none present)' } else { (@($clearedEnv) -join ', ') }

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine('# Local acceptance pack (ROADMAP §6.6)')
    [void]$md.AppendLine()
    [void]$md.AppendLine('Synthetic/offline evidence only. Manual-only rows are **not** passed.')
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Run')
    [void]$md.AppendLine()
    [void]$md.AppendLine("| Field | Value |")
    [void]$md.AppendLine("|---|---|")
    [void]$md.AppendLine("| Status | $status (exit $exitCode) |")
    [void]$md.AppendLine("| Mode | $Mode |")
    [void]$md.AppendLine("| Started UTC | $($startedUtc.ToString('o')) |")
    [void]$md.AppendLine("| Finished UTC | $($finishedUtc.ToString('o')) |")
    [void]$md.AppendLine("| Machine | $machine |")
    [void]$md.AppendLine("| OS | $osDesc |")
    [void]$md.AppendLine("| dotnet | $dotnetVersion |")
    [void]$md.AppendLine("| git HEAD | $gitHead |")
    [void]$md.AppendLine("| Configuration | $Configuration |")
    [void]$md.AppendLine("| RunId | $RunId |")
    [void]$md.AppendLine("| Isolated profile | $IsolatedProfile |")
    [void]$md.AppendLine("| Artifacts | $ArtifactsDirectory |")
    [void]$md.AppendLine("| Manifest | $manifestPath |")
    [void]$md.AppendLine("| Live profile | $(Get-LiveProfileDirectory) |")
    [void]$md.AppendLine("| Live profile use | never used as app profile; never written |")
    [void]$md.AppendLine("| Live metadata snapshot | path/length/LastWriteTimeUtc ticks; not a content hash |")
    [void]$md.AppendLine("| Live metadata unchanged | $liveUnchanged |")
    [void]$md.AppendLine("| Test step restore | --no-restore --no-build (no NuGet feed in this step) |")
    [void]$md.AppendLine("| Cleared cloud/env vars | $clearedText |")
    [void]$md.AppendLine("| Filter | ``$filterUsed`` |")
    [void]$md.AppendLine("| Tests total | $total |")
    [void]$md.AppendLine("| Passed | $passed |")
    [void]$md.AppendLine("| Failed | $failed |")
    [void]$md.AppendLine("| Skipped | $skipped |")
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Commands')
    [void]$md.AppendLine()
    foreach ($cmd in $commands) {
        [void]$md.AppendLine('```')
        [void]$md.AppendLine($cmd)
        [void]$md.AppendLine('```')
        [void]$md.AppendLine()
    }
    [void]$md.AppendLine('## Coverage matrix')
    [void]$md.AppendLine()
    [void]$md.AppendLine((Get-CoverageMatrixMarkdown))
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Manual-only checklist (open)')
    [void]$md.AppendLine()
    [void]$md.AppendLine((Get-ManualChecklistMarkdown))
    [void]$md.AppendLine()
    if (-not $liveUnchanged) {
        [void]$md.AppendLine('## LIVE PROFILE CHANGED')
        [void]$md.AppendLine()
        [void]$md.AppendLine('The metadata-only snapshot of `%LOCALAPPDATA%\QuickNotes` (path/length/mtime, not a content hash) changed. The runner never uses that directory as a profile and does not write it; fail-closed on detected metadata mutation.')
        [void]$md.AppendLine()
    }

    [System.IO.File]::WriteAllText($manifestPath, $md.ToString())

    try {
        Remove-OwnedDirectory -Path $tempRoot -OwnedRoot $tempRoot
    }
    catch {
        Write-Warning $_.Exception.Message
    }
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Manifest was not written: $manifestPath"
}

Write-Output "Local acceptance $Mode $status. Manifest: $manifestPath"
exit $exitCode
