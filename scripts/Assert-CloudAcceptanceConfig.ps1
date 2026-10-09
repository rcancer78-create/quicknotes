<#
.SYNOPSIS
Read-only local proof that the cloud acceptance workflow is safe:
triggers, permissions, secret handling, runner invocation, timeout/concurrency,
artifact allowlist, step order, and no ordinary-CI coupling. Does not call GitHub
and does not require credentials or network.

Checks actual YAML keys and step blocks after dropping comments, so commented-out
push/PR/dotnet test text cannot satisfy or hide a requirement.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$errors = New-Object System.Collections.Generic.List[string]
function Add-Err([string]$Message) { [void]$errors.Add($Message) }

function Get-YamlLines([string]$Raw) {
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($rawLine in ($Raw -split "`r?`n")) {
        if ($null -eq $rawLine) { continue }
        $indent = 0
        while ($indent -lt $rawLine.Length -and $rawLine[$indent] -eq ' ') { $indent++ }
        $content = $rawLine.Substring($indent)
        $inSingle = $false
        $inDouble = $false
        $cut = $content.Length
        for ($i = 0; $i -lt $content.Length; $i++) {
            $c = $content[$i]
            if ($c -eq "'" -and -not $inDouble) { $inSingle = -not $inSingle }
            elseif ($c -eq '"' -and -not $inSingle) { $inDouble = -not $inDouble }
            elseif ($c -eq '#' -and -not $inSingle -and -not $inDouble -and ($i -eq 0 -or [char]::IsWhiteSpace($content[$i - 1]))) {
                $cut = $i
                break
            }
        }
        $content = $content.Substring(0, $cut).TrimEnd()
        if ([string]::IsNullOrWhiteSpace($content) -or $content.StartsWith('#')) { continue }
        [void]$result.Add([pscustomobject]@{ Indent = $indent; Content = $content })
    }
    return $result
}

function Split-YamlKeyValue([string]$Content) {
    $inSingle = $false
    $inDouble = $false
    $colon = -1
    for ($i = 0; $i -lt $Content.Length; $i++) {
        $c = $Content[$i]
        if ($c -eq "'" -and -not $inDouble) { $inSingle = -not $inSingle }
        elseif ($c -eq '"' -and -not $inSingle) { $inDouble = -not $inDouble }
        elseif ($c -eq ':' -and -not $inSingle -and -not $inDouble) {
            $colon = $i
            break
        }
    }
    if ($colon -lt 0) { throw "Expected key: in workflow YAML: $Content" }
    $key = $Content.Substring(0, $colon).Trim().Trim('''"')
    $inline = $Content.Substring($colon + 1).Trim()
    return @{ Key = $key; Inline = $inline }
}

function Get-YamlUnquote([string]$Value) {
    if ($Value.Length -ge 2 -and (($Value.StartsWith('"') -and $Value.EndsWith('"')) -or ($Value.StartsWith("'") -and $Value.EndsWith("'")))) {
        return $Value.Substring(1, $Value.Length - 2)
    }
    return $Value
}

function Parse-YamlValue {
    param($Lines, [ref]$Index, [int]$KeyIndent, [string]$Inline)

    if ($Inline -eq '|' -or $Inline -eq '>') {
        $sb = New-Object System.Text.StringBuilder
        while ($Index.Value -lt $Lines.Count -and $Lines[$Index.Value].Indent -gt $KeyIndent) {
            if ($sb.Length -gt 0) { [void]$sb.Append("`n") }
            [void]$sb.Append($Lines[$Index.Value].Content)
            $Index.Value++
        }
        return @{ Kind = 'scalar'; Value = $sb.ToString() }
    }
    if ($Inline.Length -gt 0) {
        return @{ Kind = 'scalar'; Value = (Get-YamlUnquote $Inline) }
    }
    if ($Index.Value -ge $Lines.Count -or $Lines[$Index.Value].Indent -le $KeyIndent) {
        return @{ Kind = 'scalar'; Value = '' }
    }
    if ($Lines[$Index.Value].Content.StartsWith('- ')) {
        return Parse-YamlSequence -Lines $Lines -Index $Index -ParentIndent $KeyIndent
    }
    return Parse-YamlMapping -Lines $Lines -Index $Index -ParentIndent $KeyIndent
}

function Parse-YamlMapping {
    param($Lines, [ref]$Index, [int]$ParentIndent)
    $map = [ordered]@{}
    if ($Index.Value -ge $Lines.Count) { return @{ Kind = 'mapping'; Value = $map } }
    $mapIndent = $Lines[$Index.Value].Indent
    if ($mapIndent -le $ParentIndent) { return @{ Kind = 'mapping'; Value = $map } }
    while ($Index.Value -lt $Lines.Count -and $Lines[$Index.Value].Indent -eq $mapIndent -and -not $Lines[$Index.Value].Content.StartsWith('- ')) {
        $kv = Split-YamlKeyValue $Lines[$Index.Value].Content
        $Index.Value++
        $map[$kv.Key] = Parse-YamlValue -Lines $Lines -Index $Index -KeyIndent $mapIndent -Inline $kv.Inline
    }
    return @{ Kind = 'mapping'; Value = $map }
}

function Parse-YamlSequence {
    param($Lines, [ref]$Index, [int]$ParentIndent)
    $items = New-Object System.Collections.Generic.List[object]
    $seqIndent = $Lines[$Index.Value].Indent
    if ($seqIndent -le $ParentIndent) { return @{ Kind = 'sequence'; Value = $items } }
    while ($Index.Value -lt $Lines.Count -and $Lines[$Index.Value].Indent -eq $seqIndent -and $Lines[$Index.Value].Content.StartsWith('- ')) {
        $rest = $Lines[$Index.Value].Content.Substring(2).TrimStart()
        $Index.Value++
        if ([string]::IsNullOrWhiteSpace($rest)) {
            [void]$items.Add((Parse-YamlValue -Lines $Lines -Index $Index -KeyIndent $seqIndent -Inline ''))
            continue
        }
        if ($rest -notmatch ':') {
            [void]$items.Add(@{ Kind = 'scalar'; Value = (Get-YamlUnquote $rest) })
            continue
        }
        $kv = Split-YamlKeyValue $rest
        $item = [ordered]@{}
        $item[$kv.Key] = Parse-YamlValue -Lines $Lines -Index $Index -KeyIndent $seqIndent -Inline $kv.Inline
        $itemKeyIndent = $seqIndent + 2
        while ($Index.Value -lt $Lines.Count -and $Lines[$Index.Value].Indent -ge $itemKeyIndent -and -not $Lines[$Index.Value].Content.StartsWith('- ')) {
            if ($Lines[$Index.Value].Indent -ne $itemKeyIndent) { break }
            $next = Split-YamlKeyValue $Lines[$Index.Value].Content
            $Index.Value++
            $item[$next.Key] = Parse-YamlValue -Lines $Lines -Index $Index -KeyIndent $itemKeyIndent -Inline $next.Inline
        }
        [void]$items.Add(@{ Kind = 'mapping'; Value = $item })
    }
    return @{ Kind = 'sequence'; Value = $items }
}

function Get-NodeMap($Node) {
    if ($null -eq $Node) { return [ordered]@{} }
    if ($Node.Kind -eq 'mapping') { return $Node.Value }
    return [ordered]@{}
}

function Get-NodeScalar($Node) {
    if ($null -eq $Node) { return '' }
    if ($Node.Kind -eq 'scalar') { return [string]$Node.Value }
    return ''
}

function Get-NodeSeq($Node) {
    $items = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Node -or $Node.Kind -ne 'sequence' -or $null -eq $Node.Value) {
        return $items
    }
    foreach ($item in $Node.Value) {
        [void]$items.Add($item)
    }
    return $items
}

function Test-AlwaysIf([string]$Condition) {
    if ([string]::IsNullOrWhiteSpace($Condition)) { return $false }
    $t = $Condition.Trim()
    return ($t -eq 'always()' -or $t -eq '${{ always() }}')
}

function Test-HasToken([string]$Text, [string]$Token) {
    return ($Text -and $Text.ToLowerInvariant().Contains($Token.ToLowerInvariant()))
}

function Test-CoversScenario([string]$Condition, [string]$Scenario) {
    if ([string]::IsNullOrWhiteSpace($Condition)) { return $false }
    return $Condition.Contains("'$Scenario'") -or $Condition.Contains("`"$Scenario`"")
}

$workflow = Join-Path $RepoRoot '.github\workflows\cloud-acceptance.yml'
$ciWorkflow = Join-Path $RepoRoot '.github\workflows\ci.yml'
$gitignore = Join-Path $RepoRoot '.gitignore'
$encryptTool = Join-Path $RepoRoot 'QuickNotes.Tools\EncryptCredentialsTool.cs'
$protocol = Join-Path $RepoRoot 'scripts\CloudRunProtocol.ps1'
$smokeRunner = Join-Path $RepoRoot 'scripts\Run-YandexCloudLiveSmoke.ps1'
$acceptanceRunner = Join-Path $RepoRoot 'scripts\Run-YandexCloudLiveAcceptance.ps1'

foreach ($path in @($workflow, $ciWorkflow, $gitignore, $encryptTool, $protocol, $smokeRunner, $acceptanceRunner)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Add-Err "Missing required file: $path"
    }
}

if (Test-Path -LiteralPath $workflow) {
    $yml = Get-Content -LiteralPath $workflow -Raw
    $lines = Get-YamlLines $yml
    $idx = 0
    $root = Parse-YamlMapping -Lines $lines -Index ([ref]$idx) -ParentIndent -1
    $rootMap = Get-NodeMap $root

    $triggerKeys = @()
    if ($rootMap.Contains('on')) {
        $onNode = $rootMap['on']
        if ($onNode.Kind -eq 'scalar' -and $onNode.Value) { $triggerKeys += [string]$onNode.Value }
        else { $triggerKeys += @((Get-NodeMap $onNode).Keys) }
    }
    if ($triggerKeys -notcontains 'workflow_dispatch') { Add-Err 'Cloud workflow must declare workflow_dispatch as an on: key.' }
    foreach ($forbidden in @('push', 'pull_request', 'schedule', 'workflow_call')) {
        if ($triggerKeys -contains $forbidden) { Add-Err "Cloud workflow must not trigger on $forbidden." }
    }

    if (-not $rootMap.Contains('permissions')) {
        Add-Err 'Cloud workflow must declare permissions.'
    }
    else {
        $perm = $rootMap['permissions']
        $permMap = Get-NodeMap $perm
        $permScalar = Get-NodeScalar $perm
        if ($perm.Kind -ne 'mapping' -or $permMap.Count -ne 1 -or -not $permMap.Contains('contents') -or (Get-NodeScalar $permMap['contents']) -ne 'read') {
            Add-Err 'Cloud workflow permissions must be exactly contents: read.'
        }
        $null = $permScalar
    }

    if (-not $rootMap.Contains('concurrency')) {
        Add-Err 'Cloud workflow must declare concurrency.'
    }
    else {
        $conc = Get-NodeMap $rootMap['concurrency']
        if (-not $conc.Contains('cancel-in-progress')) { Add-Err 'Cloud workflow must set cancel-in-progress.' }
    }

    $jobs = Get-NodeMap $rootMap['jobs']
    if ($jobs.Count -eq 0) { Add-Err 'Cloud workflow must declare a job.' }

    foreach ($jobKey in $jobs.Keys) {
        $job = Get-NodeMap $jobs[$jobKey]
        $timeout = if ($job.Contains('timeout-minutes')) { Get-NodeScalar $job['timeout-minutes'] } else { '' }
        if ([string]::IsNullOrWhiteSpace($timeout)) { Add-Err 'Cloud workflow job must set timeout-minutes.' }
        $environment = ''
        if ($job.Contains('environment')) {
            if ($job['environment'].Kind -eq 'mapping') {
                $environment = Get-NodeScalar (Get-NodeMap $job['environment'])['name']
            }
            else {
                $environment = Get-NodeScalar $job['environment']
            }
        }
        if ($environment -ne 'cloud-acceptance') { Add-Err 'Cloud workflow job must use environment: cloud-acceptance.' }

        $steps = Get-NodeSeq $job['steps']
        if ($steps.Count -eq 0) {
            Add-Err 'Cloud workflow job must declare steps.'
            continue
        }

        $parsed = @()
        foreach ($item in $steps) {
            $map = Get-NodeMap $item
            $withMap = Get-NodeMap $map['with']
            $with = @{}
            foreach ($wk in $withMap.Keys) { $with[$wk] = Get-NodeScalar $withMap[$wk] }
            $envMap = Get-NodeMap $map['env']
            $env = @{}
            foreach ($ek in $envMap.Keys) { $env[$ek] = Get-NodeScalar $envMap[$ek] }
            $uses = Get-NodeScalar $map['uses']
            $parsed += [pscustomobject]@{
                Name            = Get-NodeScalar $map['name']
                If              = Get-NodeScalar $map['if']
                Uses            = $uses
                Run             = Get-NodeScalar $map['run']
                ContinueOnError = Get-NodeScalar $map['continue-on-error']
                With            = $with
                Env             = $env
                IsUpload        = ($uses -and $uses.StartsWith('actions/upload-artifact'))
            }
        }

        $restore = -1; $build = -1; $encrypt = -1; $smoke = -1; $acceptance = -1; $summary = -1; $upload = -1; $cleanup = -1
        $smokeCount = 0; $acceptanceCount = 0
        for ($i = 0; $i -lt $parsed.Count; $i++) {
            $s = $parsed[$i]
            if ((Test-HasToken $s.Run 'dotnet restore') -and $restore -lt 0) { $restore = $i }
            if ((Test-HasToken $s.Run 'dotnet build') -and $build -lt 0) { $build = $i }
            if ((Test-HasToken $s.Run 'encrypt-credentials') -and $encrypt -lt 0) { $encrypt = $i }
            if (Test-HasToken $s.Run 'Run-YandexCloudLiveSmoke.ps1') { $smokeCount++; if ($smoke -lt 0) { $smoke = $i } }
            if (Test-HasToken $s.Run 'Run-YandexCloudLiveAcceptance.ps1') { $acceptanceCount++; if ($acceptance -lt 0) { $acceptance = $i } }
            if ((Test-HasToken $s.Run 'Write-TestCategorySummary.ps1') -and $summary -lt 0) { $summary = $i }
            if ($s.IsUpload -and $upload -lt 0) { $upload = $i }
            if (($s.Name -eq 'Cleanup credentials' -or ((Test-HasToken $s.Name 'Cleanup') -and (Test-HasToken $s.Run 'qn-cloud-creds'))) -and $cleanup -lt 0) { $cleanup = $i }
        }

        if ($restore -lt 0) { Add-Err 'Cloud workflow must have a restore step before --no-restore build/test.' }
        if ($build -lt 0) { Add-Err 'Cloud workflow must have a build step.' }
        if ($restore -ge 0 -and $build -ge 0 -and $restore -gt $build) { Add-Err 'Restore must run before build.' }
        if ($build -ge 0 -and -not (Test-HasToken $parsed[$build].Run '--no-restore')) { Add-Err 'Build must use --no-restore after a clean restore.' }
        if ($encrypt -lt 0) { Add-Err 'Cloud workflow must invoke encrypt-credentials.' }
        if ($smokeCount -ne 1) { Add-Err 'Cloud workflow must invoke Run-YandexCloudLiveSmoke.ps1 exactly once (Smoke must not run twice).' }
        if ($acceptanceCount -ne 1) { Add-Err 'Cloud workflow must invoke Run-YandexCloudLiveAcceptance.ps1 exactly once.' }

        if ($smoke -ge 0) {
            $step = $parsed[$smoke]
            if (-not (Test-CoversScenario $step.If 'Smoke') -or -not (Test-CoversScenario $step.If 'All') -or ((Test-CoversScenario $step.If 'Acceptance') -and -not (Test-CoversScenario $step.If 'Smoke'))) {
                Add-Err 'Smoke runner step must be gated to Smoke or All.'
            }
            if (-not (Test-HasToken $step.Run '-NoRestore') -or -not (Test-HasToken $step.Run '-NoBuild')) { Add-Err 'Smoke runner invocation must pass -NoRestore and -NoBuild.' }
            if (-not (Test-HasToken $step.Run 'cloud-smoke.trx')) { Add-Err 'Smoke runner must use deterministic TRX name cloud-smoke.trx.' }
            if (-not (Test-HasToken $step.Run '-SanitizedReportFileName') -or -not (Test-HasToken $step.Run 'cloud-smoke.md')) { Add-Err 'Smoke runner must write deterministic sanitized report cloud-smoke.md.' }
            if (-not (Test-HasToken $step.Run '-BlameHangTimeout')) { Add-Err 'Smoke runner must pass -BlameHangTimeout.' }
            if (Test-HasToken $step.Run 'ProtocolSelfTest') { Add-Err 'Cloud workflow must not pass ProtocolSelfTest.' }
        }
        if ($acceptance -ge 0) {
            $step = $parsed[$acceptance]
            if (-not (Test-CoversScenario $step.If 'Acceptance') -or -not (Test-CoversScenario $step.If 'All')) {
                Add-Err 'Acceptance runner step must be gated to Acceptance or All.'
            }
            if (-not (Test-HasToken $step.Run '-NoRestore') -or -not (Test-HasToken $step.Run '-NoBuild')) { Add-Err 'Acceptance runner invocation must pass -NoRestore and -NoBuild.' }
            if (-not (Test-HasToken $step.Run 'cloud-acceptance.trx')) { Add-Err 'Acceptance runner must use deterministic TRX name cloud-acceptance.trx.' }
            if (-not (Test-HasToken $step.Run '-SanitizedReportFileName') -or -not (Test-HasToken $step.Run 'cloud-acceptance.md')) { Add-Err 'Acceptance runner must write deterministic sanitized report cloud-acceptance.md.' }
            if (Test-HasToken $step.Run 'ProtocolSelfTest') { Add-Err 'Cloud workflow must not pass ProtocolSelfTest.' }
        }
        if ($smoke -ge 0 -and $acceptance -ge 0 -and $smoke -gt $acceptance) { Add-Err 'Smoke step must precede Acceptance so All does not reverse order.' }

        $hasSecretEnv = $false
        $hasVarEnv = $false
        foreach ($s in $parsed) {
            if ($s.ContinueOnError -eq 'true') { Add-Err "Step '$($s.Name)' must not continue-on-error." }
            if ($s.Run -match '(?i)\bdotnet\s+test\b' -or $s.Run -match '(?i)--filter\b') {
                Add-Err "Step '$($s.Name)' must not run scenario dotnet test or --filter in YAML."
            }
            if ($s.Run -match '\$\{\{\s*secrets\.' -or $s.Run -match '(?i)--password\b' -or $s.Run -match '(?im)^[^\n]*(?:dotnet\s+|&\s+|\.exe\b)[^\n]*\$env:S3_(?:ACCESS_KEY_ID|SECRET_ACCESS_KEY)') {
                Add-Err "Step '$($s.Name)' must not pass secrets as CLI arguments."
            }
            if ((Test-HasToken $s.Run 'echo') -and ((Test-HasToken $s.Run 'S3_ACCESS_KEY_ID') -or (Test-HasToken $s.Run 'S3_SECRET_ACCESS_KEY'))) {
                Add-Err "Step '$($s.Name)' must not echo secrets."
            }
            if ($s.Env.ContainsKey('S3_ACCESS_KEY_ID') -and $s.Env.ContainsKey('S3_SECRET_ACCESS_KEY')) { $hasSecretEnv = $true }
            if ($s.Env.ContainsKey('S3_ENDPOINT') -and $s.Env.ContainsKey('S3_BUCKET')) { $hasVarEnv = $true }
        }
        if (-not $hasSecretEnv) { Add-Err 'Cloud workflow must map S3_ACCESS_KEY_ID and S3_SECRET_ACCESS_KEY through step env.' }
        if (-not $hasVarEnv) { Add-Err 'Cloud workflow must map S3_ENDPOINT and S3_BUCKET through step env.' }

        if ($upload -lt 0) { Add-Err 'Cloud workflow must upload artifacts.' }
        else {
            $step = $parsed[$upload]
            if (-not (Test-AlwaysIf $step.If)) { Add-Err 'Artifact upload must use if: always().' }
            $days = 0
            if (-not $step.With.ContainsKey('retention-days') -or -not [int]::TryParse($step.With['retention-days'], [ref]$days) -or $days -lt 1 -or $days -gt 14) {
                Add-Err 'Artifact retention-days must be a short window (1-14).'
            }
            if (-not $step.With.ContainsKey('path')) { Add-Err 'Artifact upload must declare path.' }
            else {
                $allowed = @(
                    'artifacts/ci/cloud-acceptance/cloud-smoke.trx',
                    'artifacts/ci/cloud-acceptance/cloud-acceptance.trx',
                    'artifacts/ci/cloud-acceptance/cloud-smoke.md',
                    'artifacts/ci/cloud-acceptance/cloud-acceptance.md',
                    'artifacts/ci/cloud-acceptance/category-summary.md'
                )
                $seen = @{}
                foreach ($item in @($step.With['path'] -split "`r?`n")) {
                    $normalized = $item.Trim().Replace('\', '/')
                    if (-not $normalized) { continue }
                    if ($normalized -match '\*\*' -or $normalized.Contains('*')) {
                        Add-Err 'Artifact upload must not use a recursive or wildcard directory glob.'
                    }
                    if ($allowed -notcontains $normalized) {
                        Add-Err "Artifact upload path is outside the TRX/sanitized-report/category-summary allowlist: $item"
                    }
                    $seen[$normalized] = $true
                    if ($normalized -match 'RUNNER_TEMP' -or $normalized -match 'qn-cloud-creds') {
                        Add-Err 'Credentials path must not be uploaded.'
                    }
                }
                foreach ($required in $allowed) {
                    if (-not $seen.ContainsKey($required)) {
                        Add-Err "Artifact upload allowlist must include $required"
                    }
                }
            }
        }

        if ($cleanup -lt 0) { Add-Err 'Cloud workflow must have a credentials cleanup step.' }
        elseif (-not (Test-AlwaysIf $parsed[$cleanup].If)) { Add-Err 'Cleanup must use if: always().' }
        if ($summary -ge 0 -and -not (Test-AlwaysIf $parsed[$summary].If)) { Add-Err 'Category summary must use if: always().' }
        if ($upload -ge 0 -and $cleanup -ge 0 -and $cleanup -lt $upload) { Add-Err 'Cleanup must run after artifact upload, not before.' }
        if ($encrypt -ge 0 -and $upload -ge 0 -and $encrypt -gt $upload) { Add-Err 'Credentials encryption must happen before upload.' }
        if ($restore -ge 0 -and $smoke -ge 0 -and $restore -gt $smoke) { Add-Err 'Restore must run before smoke.' }
        if ($build -ge 0 -and $smoke -ge 0 -and $build -gt $smoke) { Add-Err 'Build must run before smoke.' }
    }
}

if (Test-Path -LiteralPath $ciWorkflow) {
    $ciLines = Get-YamlLines (Get-Content -LiteralPath $ciWorkflow -Raw)
    $ciIdx = 0
    $ciRoot = Get-NodeMap (Parse-YamlMapping -Lines $ciLines -Index ([ref]$ciIdx) -ParentIndent -1)
    $ciText = ''
    if ($ciRoot.Contains('jobs')) {
        foreach ($job in (Get-NodeMap $ciRoot['jobs']).Values) {
            $jobMap = Get-NodeMap $job
            foreach ($step in (Get-NodeSeq $jobMap['steps'])) {
                $ciText += (Get-NodeScalar (Get-NodeMap $step)['run']) + "`n"
                $ciText += (Get-NodeScalar (Get-NodeMap $step)['uses']) + "`n"
                $ciText += (Get-NodeScalar (Get-NodeMap $step)['name']) + "`n"
            }
        }
    }
    if ($ciText -match 'cloud-acceptance') {
        Add-Err 'Ordinary CI workflow must not reference cloud-acceptance workflow or environment.'
    }
}

if (Test-Path -LiteralPath $gitignore) {
    $ignore = Get-Content -LiteralPath $gitignore -Raw
    if ($ignore -notmatch '(?m)^artifacts/') { Add-Err '.gitignore must ignore artifacts/.' }
}

if (Test-Path -LiteralPath $encryptTool) {
    $src = Get-Content -LiteralPath $encryptTool -Raw
    if ($src -notmatch 'ProtectedData\.Protect') { Add-Err 'EncryptCredentialsTool must use DPAPI ProtectedData.Protect.' }
    if ($src -notmatch 'Array\.Clear') { Add-Err 'EncryptCredentialsTool must zero sensitive buffers.' }
    if ($src -notmatch 'DataProtectionScope\.CurrentUser') { Add-Err 'EncryptCredentialsTool must use CurrentUser DPAPI scope.' }
    if ($src -notmatch 'IsNullOrWhiteSpace') { Add-Err 'EncryptCredentialsTool must reject empty credential strings.' }
    if ($src -notmatch 'File\.Move') { Add-Err 'EncryptCredentialsTool must use atomic file replacement.' }
}

if (Test-Path -LiteralPath $protocol) {
    $proto = Get-Content -LiteralPath $protocol -Raw
    if ($proto -notmatch 'cloud-run-protocol/v1') { Add-Err 'CloudRunProtocol.ps1 must declare schema cloud-run-protocol/v1.' }
    if ($proto -notmatch 'Get-QnCloudBucketFingerprint') { Add-Err 'CloudRunProtocol.ps1 must fingerprint buckets without storing the raw name.' }
    if ($proto -notmatch 'Get-QnCloudEndpointHost') { Add-Err 'CloudRunProtocol.ps1 must record endpoint host only.' }
    if ($proto -notmatch 'File\]::Replace') { Add-Err 'CloudRunProtocol.ps1 must replace reports atomically.' }
    if ($proto -notmatch 'Hard cancellation or runner loss') { Add-Err 'CloudRunProtocol.ps1 must state hard cancellation/runner loss limitations.' }
    if ($proto -notmatch 'Refusing to replace an existing protocol report with a different run id') { Add-Err 'CloudRunProtocol.ps1 must preserve run id across updates.' }
    if ($proto -notmatch 'Refusing to replace an existing protocol report with a different start time') { Add-Err 'CloudRunProtocol.ps1 must preserve start time across updates.' }
    if ($proto -notmatch 'Read-QnExistingProtocolIdentity') { Add-Err 'CloudRunProtocol.ps1 must read prior report identity before replacement.' }
    if ($proto -notmatch 'Assert-QnSafeCallerField') { Add-Err 'CloudRunProtocol.ps1 must reject unsafe caller-supplied fields.' }
}

if (Test-Path -LiteralPath $smokeRunner) {
    $ps1 = Get-Content -LiteralPath $smokeRunner -Raw
    if ($ps1 -notmatch 'QUICKNOTES_LIVE_S3_REQUIRED') { Add-Err 'Smoke runner must set QUICKNOTES_LIVE_S3_REQUIRED.' }
    if ($ps1 -notmatch 'QUICKNOTES_LIVE_S3_CREDENTIALS_PATH') { Add-Err 'Smoke runner must set QUICKNOTES_LIVE_S3_CREDENTIALS_PATH.' }
    if ($ps1 -notmatch '\$CredentialsPath') { Add-Err 'Smoke runner must accept CI CredentialsPath without replacing local connection.json default.' }
    if ($ps1 -notmatch 'LOCALAPPDATA') { Add-Err 'Smoke runner must keep local connection.json behavior when CredentialsPath is omitted.' }
    if ($ps1 -notmatch 'NoRestore') { Add-Err 'Smoke runner must support -NoRestore for CI.' }
    if ($ps1 -notmatch 'BlameHangTimeout') { Add-Err 'Smoke runner must support -BlameHangTimeout for CI.' }
    if ($ps1 -notmatch 'TrxLogFileName') { Add-Err 'Smoke runner must support deterministic TRX names.' }
    if ($ps1 -notmatch 'SanitizedReportFileName') { Add-Err 'Smoke runner must support opt-in SanitizedReportFileName.' }
    if ($ps1 -notmatch 'CloudRunProtocol\.ps1') { Add-Err 'Smoke runner must invoke CloudRunProtocol.ps1.' }
    if ($ps1 -notmatch "-File', \`$protocolScript") { Add-Err 'Smoke runner must invoke CloudRunProtocol.ps1 via an isolated pwsh -File process.' }
    if ($ps1 -notmatch 'opt-in') { Add-Err 'Smoke runner must document that the protocol/report is opt-in.' }
    if ($ps1 -notmatch 'ProtocolSelfTest') { Add-Err 'Smoke runner must support ProtocolSelfTest for offline proof.' }
    if ($ps1 -notmatch "WriteState 'planned'") { Add-Err 'Smoke runner must write planned protocol state when opted in.' }
    if ($ps1 -notmatch "WriteState 'running'") { Add-Err 'Smoke runner must write running protocol state when opted in.' }
    if ($ps1 -notmatch "WriteState 'cleanup-attempted'") { Add-Err 'Smoke runner must write cleanup-attempted protocol state when opted in.' }
    if ($ps1 -notmatch 'WriteState \$state') { Add-Err 'Smoke runner must write passed/failed protocol state before cleanup when opted in.' }
}

if (Test-Path -LiteralPath $acceptanceRunner) {
    $acc = Get-Content -LiteralPath $acceptanceRunner -Raw
    if ($acc -notmatch 'Run-YandexCloudLiveSmoke\.ps1') { Add-Err 'Acceptance runner must reuse Run-YandexCloudLiveSmoke.ps1.' }
    if ($acc -notmatch 'Scenario\s*=\s*''Acceptance''') { Add-Err 'Acceptance runner must select the Acceptance scenario.' }
    if ($acc -notmatch 'SanitizedReportFileName') { Add-Err 'Acceptance runner must forward SanitizedReportFileName.' }
}

if ($errors.Count -gt 0) {
    Write-Output 'Cloud acceptance workflow configuration is NOT proven:'
    $errors | ForEach-Object { Write-Output " - $_" }
    exit 1
}

Write-Output 'Cloud acceptance workflow is locally proven without GitHub credentials.'
Write-Output 'External boundary still open: first live GitHub run cannot be asserted from this repo clone.'
Write-Output 'Cleanup is best-effort under if: always(); hard cancellation or runner loss may skip it.'
exit 0
