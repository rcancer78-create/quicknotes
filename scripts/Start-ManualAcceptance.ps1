<#
.SYNOPSIS
Starts or completes a QuickNotes manual UX acceptance run with durable artifacts.

Never uses %LOCALAPPDATA%\QuickNotes as the app profile. Does not claim DPI,
Narrator, High Contrast, live hotkeys, or live cloud as passed.

.PARAMETER Mode
Start: create a run directory, seed an isolated profile, launch the app.
Complete: scan a run directory and write completion-report.json.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Start-ManualAcceptance.ps1

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Start-ManualAcceptance.ps1 -Mode Complete -RunDirectory artifacts\manual-acceptance\<runId>
#>
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Complete')]
    [string]$Mode = 'Start',
    [string]$RepoRoot,
    [string]$Configuration = 'Release',
    [string]$RunId,
    [string]$RunDirectory,
    [string]$IsolatedProfile
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
    return Test-PathIsStrictlyUnder $c $a
}

function Test-PathIsStrictlyUnder([string]$Candidate, [string]$Ancestor) {
    $c = (Get-NormalizedFullPath $Candidate).TrimEnd('\', '/')
    $a = (Get-NormalizedFullPath $Ancestor).TrimEnd('\', '/')
    if ($c.Equals($a, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    $sep = [IO.Path]::DirectorySeparatorChar
    $alt = [IO.Path]::AltDirectorySeparatorChar
    return $c.StartsWith($a + $sep, [StringComparison]::OrdinalIgnoreCase) -or
        $c.StartsWith($a + $alt, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CompleteRunDirectory([string]$Path, [string]$ArtifactsRoot) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Run directory path cannot be empty.'
    }
    Assert-NotLiveProfileRelated $Path 'Run directory'
    $full = (Get-NormalizedFullPath $Path).TrimEnd('\', '/')
    $root = (Get-NormalizedFullPath $ArtifactsRoot).TrimEnd('\', '/')
    if (-not (Test-PathIsStrictlyUnder $full $root)) {
        throw "Complete mode run directory must be a strict descendant of $root (got $full)."
    }

    $resolvedRoot = (Get-ResolvedIsolationPath $root).TrimEnd('\', '/')
    $resolved = (Get-ResolvedIsolationPath $full).TrimEnd('\', '/')
    if (-not (Test-PathIsStrictlyUnder $resolved $resolvedRoot)) {
        throw "Complete mode run directory must resolve to a strict descendant of $root (got $Path -> $resolved)."
    }
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

function Get-IsolationNativeType {
    $typeName = 'QuickNotes.Scripts.QnIsolationPath'
    $existing = $typeName -as [type]
    if ($existing) {
        return $existing
    }
    Add-Type -Name 'QnIsolationPath' -Namespace 'QuickNotes.Scripts' -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern System.IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, System.IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, System.IntPtr hTemplateFile);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern int GetFinalPathNameByHandle(System.IntPtr hFile, System.Text.StringBuilder lpszFilePath, int cchFilePath, uint dwFlags);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool CloseHandle(System.IntPtr hObject);
'@
    return $typeName -as [type]
}

function Get-KernelFinalPath([string]$ExistingPath) {
    $native = Get-IsolationNativeType
    $handle = $native::CreateFile(
        $ExistingPath,
        [uint32]0x80,
        [uint32]7,
        [System.IntPtr]::Zero,
        [uint32]3,
        [uint32]0x02000000,
        [System.IntPtr]::Zero)
    if ($handle.ToInt64() -eq -1) {
        return $null
    }
    try {
        $buffer = New-Object System.Text.StringBuilder 512
        $chars = $native::GetFinalPathNameByHandle($handle, $buffer, $buffer.Capacity, [uint32]0)
        if ($chars -ge $buffer.Capacity) {
            [void]$buffer.EnsureCapacity($chars + 2)
            $chars = $native::GetFinalPathNameByHandle($handle, $buffer, $buffer.Capacity, [uint32]0)
        }
        if ($chars -le 0) {
            return $null
        }
        $text = $buffer.ToString()
        if ($text.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) {
            return '\\' + $text.Substring(8)
        }
        if ($text.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) {
            return $text.Substring(4)
        }
        return $text
    }
    finally {
        [void]$native::CloseHandle($handle)
    }
}

function Get-ResolvedIsolationPath([string]$Path) {
    $full = (Get-NormalizedFullPath $Path).TrimEnd('\', '/')
    $missing = New-Object System.Collections.Generic.Stack[string]
    $current = $full
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $final = Get-KernelFinalPath $current
            if ([string]::IsNullOrWhiteSpace($final)) {
                $item = Get-Item -LiteralPath $current -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Path contains a reparse point that could not be resolved: $current"
                }
                $final = $current
            }
            $resolved = (Get-NormalizedFullPath $final).TrimEnd('\', '/')
            while ($missing.Count -gt 0) {
                $resolved = Get-NormalizedFullPath (Join-Path $resolved $missing.Pop())
            }
            return $resolved.TrimEnd('\', '/')
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) {
            return $full
        }
        $leaf = Split-Path -Leaf $current
        if (-not [string]::IsNullOrWhiteSpace($leaf)) {
            $missing.Push($leaf)
        }
        $current = $parent
    }
}

function Assert-NotLiveProfileRelated([string]$Path, [string]$Role) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Role path cannot be empty."
    }
    $full = (Get-NormalizedFullPath $Path).TrimEnd('\', '/')
    $live = (Get-LiveProfileDirectory).TrimEnd('\', '/')
    foreach ($candidate in @($full, (Get-ResolvedIsolationPath $Path))) {
        $c = $candidate.TrimEnd('\', '/')
        if ($c.Equals($live, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Role must not be the live profile (including via a junction or symlink): $Path -> $c"
        }
        if (Test-PathIsUnder $c $live) {
            throw "$Role must not be inside the live profile (including via a junction or symlink): $Path -> $c"
        }
        if (Test-PathIsUnder $live $c) {
            throw "$Role must not be an ancestor of the live profile (including via a junction or symlink): $Path -> $c"
        }
    }
}

function Get-LiveProfileSnapshot {
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

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Get-NormalizedFullPath $Path), $Content, $utf8)
}

function Get-RequiredManualArtifacts {
    return @(
        'RESULTS.md',
        'session.json',
        'prep-manifest.json',
        'prep.log',
        'screenshots/m01-single-click-selected.png',
        'screenshots/m01-double-click-no-editor.png',
        'screenshots/m02-enter-detail.png',
        'screenshots/m02-ctrl-e-window.png',
        'screenshots/m03-expanded.png',
        'screenshots/m03-collapsed.png',
        'screenshots/m04-editor-tags-wide.png',
        'screenshots/m04-editor-tags-narrow.png',
        'screenshots/m05-cloud-first.png',
        'screenshots/m05-cloud-wizard.png',
        'screenshots/m05-cloud-advanced.png',
        'screenshots/m06-transfer-export.png',
        'screenshots/m06-transfer-archive-dark.png',
        'screenshots/m07-tray.png',
        'screenshots/m07-unsaved-esc.png',
        'screenshots/m08-dpi-100-main.png',
        'screenshots/m08-dpi-125-main.png',
        'screenshots/m08-dpi-150-main.png',
        'screenshots/m08-dpi-200-main.png',
        'screenshots/m08-dpi-100-cloud.png',
        'screenshots/m08-dpi-150-cloud.png',
        'screenshots/m09-hc-main.png',
        'screenshots/m09-hc-editor.png',
        'screenshots/m09-hc-cloud.png',
        'screenshots/m09-hc-help.png',
        'narrator/m10-expand-button.txt',
        'narrator/m10-new-note.txt',
        'narrator/m10-cloud-wizard.txt',
        'narrator/m10-restore-tag.txt'
    )
}

function Resolve-AppExe([string]$Root, [string]$Config) {
    $tfm = 'net8.0-windows10.0.19041.0'
    $exe = Join-Path $Root "QuickNotes.App\bin\$Config\$tfm\QuickNotes.App.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Missing $exe. Run: dotnet build `"$(Join-Path $Root 'QuickNotes.sln')`" -c $Config"
    }
    return Get-NormalizedFullPath $exe
}

$repo = Get-NormalizedFullPath $RepoRoot
$live = Get-LiveProfileDirectory
$artifactsRoot = Get-NormalizedFullPath (Join-Path $repo 'artifacts\manual-acceptance')
$protocol = Join-Path $repo 'docs\ux\manual-acceptance-protocol.md'
$template = Join-Path $repo 'docs\ux\manual-acceptance-results-template.md'
if (-not (Test-Path -LiteralPath $protocol)) { throw "Missing protocol: $protocol" }
if (-not (Test-Path -LiteralPath $template)) { throw "Missing template: $template" }

if ($Mode -eq 'Complete') {
    if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
        throw 'Complete mode requires -RunDirectory pointing at artifacts/manual-acceptance/<runId>.'
    }
    $runDir = Get-NormalizedFullPath $RunDirectory
    if (-not (Test-Path -LiteralPath $runDir)) {
        throw "Run directory does not exist: $runDir"
    }
    Assert-CompleteRunDirectory $runDir $artifactsRoot

    $present = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($relative in (Get-RequiredManualArtifacts)) {
        $full = Join-Path $runDir ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (Test-Path -LiteralPath $full) {
            $item = Get-Item -LiteralPath $full
            if ($item.Length -gt 0) {
                [void]$present.Add($relative)
            }
            else {
                [void]$missing.Add($relative + ' (empty)')
            }
        }
        else {
            [void]$missing.Add($relative)
        }
    }

    $after = Get-LiveProfileSnapshot
    Write-Utf8NoBom (Join-Path $runDir 'live-profile-after-complete.txt') $after
    $beforePath = Join-Path $runDir 'live-profile-before.txt'
    $liveUnchanged = $false
    if (Test-Path -LiteralPath $beforePath) {
        $before = [System.IO.File]::ReadAllText($beforePath)
        $liveUnchanged = ($before -eq $after)
    }

    $report = [ordered]@{
        generatedUtc          = [DateTime]::UtcNow.ToString('o')
        runDirectory          = $runDir
        requiredCount         = (Get-RequiredManualArtifacts).Count
        presentCount          = $present.Count
        missingCount          = $missing.Count
        present               = @($present)
        missing               = @($missing)
        liveProfileUnchanged  = $liveUnchanged
        claimsPass            = $false
        note                  = 'This report only lists files. overallStatus in RESULTS.md is set by the tester. Optional M-11/M-12 screenshots are not required.'
    }
    $json = $report | ConvertTo-Json -Depth 6
    Write-Utf8NoBom (Join-Path $runDir 'completion-report.json') $json
    Add-Content -LiteralPath (Join-Path $runDir 'session.log') -Value ("completeUtc=" + [DateTime]::UtcNow.ToString('o') + "; missing=" + $missing.Count) -Encoding utf8
    Write-Host "Wrote $runDir\completion-report.json"
    Write-Host ("present={0} missing={1} liveUnchanged={2}" -f $present.Count, $missing.Count, $liveUnchanged)
    if ($missing.Count -gt 0) {
        Write-Host 'Missing:'
        $missing | ForEach-Object { Write-Host " - $_" }
    }
    return
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}

$runDir = if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    Get-NormalizedFullPath (Join-Path $artifactsRoot $RunId)
} else {
    Get-NormalizedFullPath $RunDirectory
}
Assert-NotLiveProfileRelated $runDir 'Run directory'
if (-not (Test-PathIsUnder $runDir $artifactsRoot) -and -not (Test-PathIsUnder $runDir (Join-Path $repo 'artifacts'))) {
    throw "Run directory must stay under $artifactsRoot (got $runDir)"
}

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $runDir 'screenshots') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $runDir 'narrator') | Out-Null

$profileDir = if ([string]::IsNullOrWhiteSpace($IsolatedProfile)) {
    Get-NormalizedFullPath (Join-Path $runDir 'profile')
} else {
    Get-NormalizedFullPath $IsolatedProfile
}
Assert-NotLiveProfileRelated $profileDir 'Isolated profile'
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null

$exe = Resolve-AppExe $repo $Configuration
$startedUtc = [DateTime]::UtcNow.ToString('o')
$before = Get-LiveProfileSnapshot
Write-Utf8NoBom (Join-Path $runDir 'live-profile-before.txt') $before

$results = [System.IO.File]::ReadAllText($template)
$results = $results.Replace('{{RUN_ID}}', $RunId)
$results = $results.Replace('{{STARTED_UTC}}', $startedUtc)
$results = $results.Replace('{{PROFILE_DIR}}', $profileDir)
$results = $results.Replace('{{ARTIFACTS_DIR}}', $runDir)
$results = $results.Replace('{{EXE}}', $exe)
$results = $results.Replace('{{LIVE_PROFILE}}', $live)
Write-Utf8NoBom (Join-Path $runDir 'RESULTS.md') $results
Copy-Item -LiteralPath $protocol -Destination (Join-Path $runDir 'protocol.md')

$prepArgs = "--isolated-profile `"$profileDir`" --manual-acceptance-prep --output-dir `"$runDir`""
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = $prepArgs
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$prep = [System.Diagnostics.Process]::Start($psi)
$prepOut = $prep.StandardOutput.ReadToEnd()
$prepErr = $prep.StandardError.ReadToEnd()
if (-not $prep.WaitForExit(120000)) {
    try { $prep.Kill() } catch { }
    throw 'manual-acceptance-prep hung'
}
if ($prep.ExitCode -ne 0 -or ($prepOut -notmatch 'QN_MANUAL_ACCEPTANCE_PREP_SUCCESS')) {
    Write-Utf8NoBom (Join-Path $runDir 'prep-process.log') ($prepOut + "`n" + $prepErr)
    throw "prep failed exit=$($prep.ExitCode). See prep-process.log"
}
Write-Utf8NoBom (Join-Path $runDir 'prep-process.log') ($prepOut + "`n" + $prepErr)

$afterPrep = Get-LiveProfileSnapshot
Write-Utf8NoBom (Join-Path $runDir 'live-profile-after-prep.txt') $afterPrep
if ($before -ne $afterPrep) {
    throw 'Live profile metadata changed during prep. Aborting. Do not continue on this machine until that is understood.'
}

$launchCommand = "`"$exe`" --isolated-profile `"$profileDir`""
$session = [ordered]@{
    runId                 = $RunId
    startedUtc            = $startedUtc
    exe                   = $exe
    isolatedProfile       = $profileDir
    artifactsDirectory    = $runDir
    liveProfile           = $live
    launchCommand         = $launchCommand
    protocol              = $protocol
    mode                  = 'Start'
    claimsPhysicalDpi     = $false
    claimsNarrator        = $false
    claimsHighContrast    = $false
    claimsLiveCloud       = $false
}
Write-Utf8NoBom (Join-Path $runDir 'session.json') ($session | ConvertTo-Json -Depth 6)

$log = @(
    "QuickNotes manual acceptance",
    "runId=$RunId",
    "startedUtc=$startedUtc",
    "exe=$exe",
    "profile=$profileDir",
    "artifacts=$runDir",
    "live=$live",
    "prepExit=$($prep.ExitCode)",
    "launch=$launchCommand"
) -join [Environment]::NewLine
Write-Utf8NoBom (Join-Path $runDir 'session.log') $log

$interactive = New-Object System.Diagnostics.ProcessStartInfo
$interactive.FileName = $exe
$interactive.Arguments = "--isolated-profile `"$profileDir`""
$interactive.UseShellExecute = $true
[void][System.Diagnostics.Process]::Start($interactive)

Write-Host "Run directory: $runDir"
Write-Host "Isolated profile: $profileDir"
Write-Host "Fill RESULTS.md and save screenshots into screenshots\ as named in protocol.md"
Write-Host "When finished: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Start-ManualAcceptance.ps1 -Mode Complete -RunDirectory `"$runDir`""
