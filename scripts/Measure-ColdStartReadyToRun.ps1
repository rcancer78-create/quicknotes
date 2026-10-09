<#
.SYNOPSIS
Publishes two isolated win-x64 framework-dependent trees (baseline vs ReadyToRun) and measures cold start.

Never copies publish output into QuickNotes.App/bin or obj. Never uses %LOCALAPPDATA%\QuickNotes as a profile.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$Warmup = 2,
    [int]$Samples = 10,
    [int]$TimeoutMs = 60000,
    [switch]$Smoke,
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedFullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-SafePublishDirectory([string]$Path, [string]$Root) {
    $full = Get-NormalizedFullPath $Path
    $root = Get-NormalizedFullPath $Root
    $appBin = Get-NormalizedFullPath (Join-Path $root 'QuickNotes.App\bin')
    $appObj = Get-NormalizedFullPath (Join-Path $root 'QuickNotes.App\obj')
    $isUnder = {
        param($candidate, $ancestor)
        $c = $candidate.TrimEnd('\', '/')
        $a = $ancestor.TrimEnd('\', '/')
        return ($c.Equals($a, [StringComparison]::OrdinalIgnoreCase)) -or
            $c.StartsWith($a + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $c.StartsWith($a + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    if (& $isUnder $full $appBin) { throw "Refusing publish directory inside QuickNotes.App/bin: $full" }
    if (& $isUnder $full $appObj) { throw "Refusing publish directory inside QuickNotes.App/obj: $full" }
    $artifacts = Get-NormalizedFullPath (Join-Path $root 'artifacts\publish\cold-start')
    $temp = Get-NormalizedFullPath ([IO.Path]::GetTempPath())
    $okArtifacts = & $isUnder $full $artifacts
    $okTemp = & $isUnder $full $temp
    if (-not $okArtifacts -and -not $okTemp) {
        throw "Publish directory must be under artifacts/publish/cold-start or temp: $full"
    }
    $leaf = Split-Path -Leaf $full
    if ($okArtifacts -and $leaf -notin @('baseline', 'r2r')) {
        throw "Artifact publish leaf must be baseline or r2r: $full"
    }
}

function Get-LiveProfileFingerprint {
    $live = Join-Path $env:LOCALAPPDATA 'QuickNotes'
    if (-not (Test-Path -LiteralPath $live)) {
        return 'ABSENT'
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $lines = New-Object System.Collections.Generic.List[string]
        Get-ChildItem -LiteralPath $live -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring((Get-NormalizedFullPath $live).Length).TrimStart('\', '/').Replace('\', '/')
            $stream = [System.IO.File]::Open($_.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $hash = [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $stream.Dispose()
            }
            [void]$lines.Add("$relative|$($_.Length)|$hash")
        }
        $payload = [string]::Join("`n", $lines)
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

$repo = Get-NormalizedFullPath $RepoRoot
Set-Location $repo

$liveBefore = Get-LiveProfileFingerprint
Write-Host "Live profile hash before: $liveBefore"

$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '_' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
$publishRoot = Join-Path $repo "artifacts\publish\cold-start\$runId"
$baselineDir = Join-Path $publishRoot 'baseline'
$r2rDir = Join-Path $publishRoot 'r2r'
Assert-SafePublishDirectory -Path $baselineDir -Root $repo
Assert-SafePublishDirectory -Path $r2rDir -Root $repo

$csproj = Join-Path $repo 'QuickNotes.App\QuickNotes.App.csproj'
$toolsProj = Join-Path $repo 'QuickNotes.Tools\QuickNotes.Tools.csproj'

Write-Host 'Building QuickNotes.Tools (Release)...'
dotnet build $toolsProj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build Tools failed: $LASTEXITCODE" }

$baselineCmd = "dotnet publish `"$csproj`" -c Release -r win-x64 --self-contained false -o `"$baselineDir`" --nologo"
$r2rCmd = "dotnet publish `"$csproj`" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o `"$r2rDir`" --nologo"

if (-not $SkipPublish) {
    New-Item -ItemType Directory -Path $baselineDir -Force | Out-Null
    New-Item -ItemType Directory -Path $r2rDir -Force | Out-Null
    Assert-SafePublishDirectory -Path $baselineDir -Root $repo
    Assert-SafePublishDirectory -Path $r2rDir -Root $repo

    Write-Host $baselineCmd
    dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $baselineDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "baseline publish failed: $LASTEXITCODE" }

    Write-Host $r2rCmd
    dotnet publish $csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $r2rDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "r2r publish failed: $LASTEXITCODE" }
}

$baselineExe = Join-Path $baselineDir 'QuickNotes.App.exe'
$r2rExe = Join-Path $r2rDir 'QuickNotes.App.exe'
if (-not (Test-Path -LiteralPath $baselineExe)) { throw "Missing $baselineExe" }
if (-not (Test-Path -LiteralPath $r2rExe)) { throw "Missing $r2rExe" }

# Never copy those trees back into bin/obj.
$appBin = Join-Path $repo 'QuickNotes.App\bin'
$appObj = Join-Path $repo 'QuickNotes.App\obj'
foreach ($src in @($baselineDir, $r2rDir)) {
    $fullSrc = Get-NormalizedFullPath $src
    $fullBin = Get-NormalizedFullPath $appBin
    $fullObj = Get-NormalizedFullPath $appObj
    if ($fullSrc.StartsWith($fullBin, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path resolved inside bin: $fullSrc"
    }
    if ($fullSrc.StartsWith($fullObj, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path resolved inside obj: $fullSrc"
    }
}

$toolsExe = Join-Path $repo 'QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe'
if (-not (Test-Path -LiteralPath $toolsExe)) { throw "Missing $toolsExe" }

$report = Join-Path $repo ("docs\performance\cold-start-r2r-report-" + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd') + ".md")
$toolArgs = @(
    'cold-start-r2r',
    '--repo-root', $repo,
    '--baseline-exe', $baselineExe,
    '--r2r-exe', $r2rExe,
    '--warmup', "$Warmup",
    '--samples', "$Samples",
    '--timeout-ms', "$TimeoutMs",
    '--report', $report,
    '--baseline-publish-command', $baselineCmd,
    '--r2r-publish-command', $r2rCmd
)
if ($Smoke) { $toolArgs += '--smoke' }

Write-Host "Measuring cold start with isolated profiles..."
& $toolsExe @toolArgs
if ($LASTEXITCODE -ne 0) { throw "cold-start-r2r tool failed: $LASTEXITCODE" }

$liveAfter = Get-LiveProfileFingerprint
Write-Host "Live profile hash after:  $liveAfter"
if ($liveBefore -ne $liveAfter) {
    throw "Live profile hash changed. before=$liveBefore after=$liveAfter"
}

Write-Host 'Verifying ordinary Release build (no publish copy-back)...'
dotnet build (Join-Path $repo 'QuickNotes.sln') -c Release --nologo -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Release build after publish failed: $LASTEXITCODE" }

Write-Host "Done. Report: $report"
exit 0
