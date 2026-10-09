<#
.SYNOPSIS
Builds a disjoint unit/integration/UiSensitive/LiveCloud summary from a single TRX run.
Exits non-zero when the TRX is missing or any test failed/errored so a green summary cannot hide a red run.
xUnit TRX from VSTest often omits traits, so class-level TestCategory attributes in QuickNotes.Tests are the fallback map.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxDirectory,

    [string]$OutputPath,

    [string]$RepoRoot
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

function Get-Attr([System.Xml.XmlNode]$Node, [string]$Name) {
    if ($null -eq $Node -or $null -eq $Node.Attributes) { return '' }
    $attr = $Node.Attributes.GetNamedItem($Name)
    if ($null -eq $attr) { return '' }
    return [string]$attr.Value
}

function Get-DeclaredClassCategories([string]$TestsRoot) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $TestsRoot)) {
        return $map
    }

    $pattern = '(?:\[[^\]]+\]\s*)*\[TestCategory\(TestCategories\.(Unit|Integration|UiSensitive|LiveCloud)\)\]\s*(?:\[[^\]]+\]\s*)*public (?:sealed )?class (\w+)'
    foreach ($file in Get-ChildItem -LiteralPath $TestsRoot -Filter *.cs -File -Recurse) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, $pattern)) {
            $map[$match.Groups[2].Value] = $match.Groups[1].Value
        }
    }

    return $map
}

function Get-ShortClassName([string]$ClassName) {
    if ([string]::IsNullOrWhiteSpace($ClassName)) { return '' }
    $name = $ClassName.Split('.')[-1]
    return $name.Split('+')[-1]
}

function Get-CategoryFromNode([System.Xml.XmlNode]$UnitTestNode) {
    $raw = New-Object System.Collections.Generic.List[string]

    foreach ($item in @($UnitTestNode.GetElementsByTagName('TestCategoryItem'))) {
        $named = Get-Attr $item 'TestCategory'
        if ($named) { [void]$raw.Add($named) }
        if ($item.InnerText) { [void]$raw.Add([string]$item.InnerText) }
    }

    foreach ($prop in @($UnitTestNode.GetElementsByTagName('Property'))) {
        $keyNode = @($prop.GetElementsByTagName('Key')) | Select-Object -First 1
        $valueNode = @($prop.GetElementsByTagName('Value')) | Select-Object -First 1
        $keyText = if ($keyNode) { [string]$keyNode.InnerText } else { '' }
        $valueText = if ($valueNode) { [string]$valueNode.InnerText } else { '' }
        if ($keyText -match 'Trait|Category') {
            [void]$raw.Add($valueText)
            if ($valueText) { [void]$raw.Add("$keyText`:$valueText") }
        }
    }

    $normalized = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $raw) {
        if ([string]::IsNullOrWhiteSpace($item)) { continue }
        $value = $item.Trim()
        $open = $value.IndexOf('[')
        $close = $value.IndexOf(']')
        if ($open -ge 0 -and $close -gt $open) {
            $value = $value.Substring($open + 1, $close - $open - 1).Trim()
        }
        elseif ($value -like 'Category:*') {
            $value = $value.Substring('Category:'.Length).Trim()
        }

        foreach ($known in @('LiveCloud', 'UiSensitive', 'Integration', 'Unit')) {
            if ($value.Equals($known, [StringComparison]::OrdinalIgnoreCase)) {
                [void]$normalized.Add($known)
            }
        }
    }

    if ($normalized.Contains('LiveCloud')) { return 'LiveCloud' }
    if ($normalized.Contains('UiSensitive')) { return 'UiSensitive' }
    if ($normalized.Contains('Integration')) { return 'Integration' }
    return 'Unit'
}

function Resolve-ResultCategory([System.Xml.XmlNode]$UnitTestNode, [hashtable]$Declared) {
    $fromTraits = Get-CategoryFromNode $UnitTestNode
    $method = @($UnitTestNode.GetElementsByTagName('TestMethod')) | Select-Object -First 1
    $shortName = Get-ShortClassName (Get-Attr $method 'className')
    $fromClass = 'Unit'
    if ($shortName -and $Declared.ContainsKey($shortName)) {
        $fromClass = [string]$Declared[$shortName]
    }

    if ($fromTraits -eq 'LiveCloud' -or $fromClass -eq 'LiveCloud') { return 'LiveCloud' }
    if ($fromTraits -eq 'UiSensitive' -or $fromClass -eq 'UiSensitive') { return 'UiSensitive' }
    if ($fromTraits -eq 'Integration' -or $fromClass -eq 'Integration') { return 'Integration' }
    return 'Unit'
}

if (-not (Test-Path -LiteralPath $TrxDirectory)) {
    Write-Output "ERROR: TRX directory does not exist: $TrxDirectory"
    exit 2
}

$trxFiles = @(Get-ChildItem -LiteralPath $TrxDirectory -Filter *.trx -File -ErrorAction SilentlyContinue)
if ($trxFiles.Count -eq 0) {
    Write-Output "ERROR: No TRX files found in $TrxDirectory"
    exit 2
}

$categories = [ordered]@{
    Unit         = @{ Total = 0; Passed = 0; Failed = 0; Error = 0; Skipped = 0; Other = 0 }
    Integration  = @{ Total = 0; Passed = 0; Failed = 0; Error = 0; Skipped = 0; Other = 0 }
    UiSensitive  = @{ Total = 0; Passed = 0; Failed = 0; Error = 0; Skipped = 0; Other = 0 }
    LiveCloud    = @{ Total = 0; Passed = 0; Failed = 0; Error = 0; Skipped = 0; Other = 0 }
}

$overallFailed = 0
$overallError = 0
$overallTotal = 0
$declared = Get-DeclaredClassCategories (Join-Path $RepoRoot 'QuickNotes.Tests')

foreach ($file in $trxFiles) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
    $defs = @{}
    foreach ($unit in @($trx.GetElementsByTagName('UnitTest'))) {
        $id = Get-Attr $unit 'id'
        if (-not $id) { continue }
        $defs[$id] = Resolve-ResultCategory $unit $declared
    }

    foreach ($result in @($trx.GetElementsByTagName('UnitTestResult'))) {
        $overallTotal++
        $testId = Get-Attr $result 'testId'
        $category = 'Unit'
        if ($testId -and $defs.ContainsKey($testId)) {
            $category = $defs[$testId]
        }
        $bucket = $categories[$category]
        $bucket.Total++
        $outcome = Get-Attr $result 'outcome'
        switch -Regex ($outcome) {
            '^(Passed)$' { $bucket.Passed++; break }
            '^(Failed)$' { $bucket.Failed++; $overallFailed++; break }
            '^(Error)$' { $bucket.Error++; $overallError++; break }
            '^(NotExecuted|Skipped|Timeout|Aborted)$' { $bucket.Skipped++; break }
            default { $bucket.Other++ }
        }
    }
}

$disjointSum = 0
foreach ($name in $categories.Keys) {
    $disjointSum += $categories[$name].Total
}

$lines = New-Object System.Collections.Generic.List[string]
[void]$lines.Add('# Test category summary')
[void]$lines.Add('')
[void]$lines.Add("Source: $TrxDirectory")
[void]$lines.Add("Total: $overallTotal")
[void]$lines.Add("Disjoint category sum: $disjointSum")
[void]$lines.Add("Failed: $overallFailed")
[void]$lines.Add("Error: $overallError")
[void]$lines.Add('')
[void]$lines.Add('| Category | Total | Passed | Failed | Error | Skipped | Other |')
[void]$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
foreach ($name in @('Unit', 'Integration', 'UiSensitive', 'LiveCloud')) {
    $b = $categories[$name]
    [void]$lines.Add("| $name | $($b.Total) | $($b.Passed) | $($b.Failed) | $($b.Error) | $($b.Skipped) | $($b.Other) |")
}
[void]$lines.Add('')
[void]$lines.Add('LiveCloud is listed even when the ordinary CI process never sets QUICKNOTES_LIVE_S3_* and therefore never opens the network.')
[void]$lines.Add('This summary is not an independent test job: it only groups the same full TRX run.')

$text = $lines -join [Environment]::NewLine
Write-Output $text

if ($OutputPath) {
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $text -Encoding utf8
}

if ($disjointSum -ne $overallTotal) {
    Write-Output "ERROR: disjoint category sum $disjointSum != total $overallTotal"
    exit 3
}

if ($overallFailed -gt 0 -or $overallError -gt 0) {
    Write-Output "ERROR: category summary cannot hide failing tests (failed=$overallFailed error=$overallError)"
    exit 1
}

exit 0
