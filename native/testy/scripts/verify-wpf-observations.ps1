#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string[]] $ReportPaths,
    [string] $OutputPath = (Join-Path $PSScriptRoot '../verification/wpf-realization-audit.json')
)

# This is an offline evidence audit. It never launches a process, attaches a driver,
# or changes an application. Incomplete or failed source reports cannot pass.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checks = [System.Collections.Generic.List[object]]::new()
$reports = [System.Collections.Generic.List[object]]::new()
$comparisons = 0
$expectedComparisons = 0
$scenarios = 0
$expectedScenarios = 0
$seenReports = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$drivers = @('Windows UI Automation', 'WPF in-process probe')
$scenarioNames = @('Grid logical lookup never realizes or scrolls', 'List logical lookup never realizes or scrolls')

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw [System.IO.InvalidDataException]::new($Message) }
}

function Read-JsonEvidence([string] $Path) {
    $absolute = [System.IO.Path]::GetFullPath($Path)
    Require (Test-Path -LiteralPath $absolute -PathType Leaf) "Missing evidence file: $absolute"
    $raw = [System.IO.File]::ReadAllText($absolute)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($raw))).Replace('-', '') }
    finally { $hasher.Dispose() }
    [pscustomobject]@{ Path = $absolute; Hash = $hash; Data = ($raw | ConvertFrom-Json) }
}

function Get-RealizedFootprint($Snapshot, [string] $ContainerId, [string] $Driver) {
    Require ($Snapshot.isTruncated -eq $false) 'A truncated tree cannot prove unchanged realization.'
    Require ([int]$Snapshot.target.processId -gt 0) 'Snapshot has no valid attached target PID.'
    $elements = @($Snapshot.elements)
    $roots = @()
    for ($i = 0; $i -lt $elements.Count; $i++) {
        if ($elements[$i].automationId -ceq $ContainerId) { $roots += $i }
    }
    Require ($roots.Count -eq 1) "Expected exactly one $ContainerId container."
    $rootIndex = $roots[0]
    $root = $elements[$rootIndex]
    Require (-not [string]::IsNullOrWhiteSpace([string]$root.controlType) -and -not [string]::IsNullOrWhiteSpace([string]$root.runtimeId)) "Container $ContainerId lacks its control type or runtime identity."
    $identities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $automationIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $members = [System.Collections.Generic.List[object]]::new()
    $rows = 0; $cells = 0
    for ($i = $rootIndex + 1; $i -lt $elements.Count -and [int]$elements[$i].depth -gt [int]$root.depth; $i++) {
        $element = $elements[$i]
        $id = [string]$element.automationId
        $isRow = if ($ContainerId -ceq 'OrdersGrid') { $id -cmatch '^Order-[0-9]{4}$' } else { $id -cmatch '^ListOrder-[0-9]{4}$' }
        $isCell = $ContainerId -ceq 'OrdersGrid' -and $id -cmatch '^Cell-Order-[0-9]{4}-.+$'
        if (-not ($isRow -or $isCell)) { continue }
        Require (-not [string]::IsNullOrWhiteSpace([string]$element.controlType)) "Missing control type for $id."
        Require (-not [string]::IsNullOrWhiteSpace([string]$element.runtimeId)) "Missing runtime identity for $id."
        if ($Driver -ceq 'WPF in-process probe') {
            Require ([string]$element.runtimeId).StartsWith("wpf:$($Snapshot.target.processId):", [StringComparison]::Ordinal) "Invalid probe runtime identity for $id."
        } else {
            Require (-not ([string]$element.runtimeId).StartsWith('wpf:', [StringComparison]::OrdinalIgnoreCase)) "Probe identity appeared in UIA evidence for $id."
        }
        Require ($automationIds.Add($id)) "Duplicate realized AutomationId $id under $ContainerId."
        $key = [string]::Join("`t", @($id, [string]$element.controlType, [string]$element.runtimeId))
        Require ($identities.Add($key)) "Duplicate realized identity for $id."
        $members.Add([pscustomobject]@{ automationId = $id; controlType = [string]$element.controlType; runtimeId = [string]$element.runtimeId; key = $key })
        if ($isRow) { $rows++ } else { $cells++ }
    }
    Require ($rows -gt 0) "No realized item identities were captured under $ContainerId."
    if ($ContainerId -ceq 'OrdersGrid') {
        Require ($cells -ge $rows) 'Baseline/observed grid lacks its materialized cell identities; cells must not be silently excluded.'
        foreach ($cell in @($members | Where-Object { $_.automationId -cmatch '^Cell-' })) {
            $rowId = [regex]::Match($cell.automationId, '^Cell-(Order-[0-9]{4})-').Groups[1].Value
            Require ($automationIds.Contains($rowId)) "Realized cell $($cell.automationId) has no realized owning row in the container."
        }
    }
    [pscustomobject]@{
        ContainerId = $ContainerId; ContainerType = [string]$root.controlType; ContainerRuntimeId = [string]$root.runtimeId
        Rows = $rows; Cells = $cells; Identities = $identities; Members = $members.ToArray()
    }
}

function Check-Snapshot($Snapshot, $Baseline, $Run, [string] $Path) {
    Require ($Snapshot.isTruncated -eq $false) "Incomplete snapshot: $Path"
    Require ([int]$Snapshot.target.processId -eq [int]$Baseline.target.processId -and [int]$Snapshot.target.processId -eq [int]$Run.target.processId) "Target identity changed in $Path"
    Require ([DateTimeOffset]$Snapshot.capturedAt -ge [DateTimeOffset]$Baseline.capturedAt) "Snapshot precedes the baseline: $Path"
}

foreach ($suppliedPath in $ReportPaths) {
    $reportInfo = $null
    try {
        $reportInfo = Read-JsonEvidence $suppliedPath
        Require ($seenReports.Add($reportInfo.Path)) 'The same report was supplied more than once.'
        $source = $reportInfo.Data
        $repeatCount = [int]$source.repetitions
        Require ($repeatCount -ge 1 -and $repeatCount -le 20) 'Report repetitions must be 1–20.'
        $expectedScenarios += 4 * $repeatCount
        $expectedComparisons += 24 * $repeatCount
        $reportProblems = [System.Collections.Generic.List[string]]::new()
        if ($null -eq $source.finishedAt -or -not $source.completedAllScenarios) { $reportProblems.Add('Report has not finished all planned scenarios.') }
        if ($source.cancelled) { $reportProblems.Add('Report was cancelled.') }
        if (-not $source.passed) { $reportProblems.Add('The source WPF report did not pass; this audit cannot promote it to a pass.') }
        if (@($source.checks).Count -ne [int]$source.plannedChecks) { $reportProblems.Add('Report check count does not equal its planned count.') }
        if (@($source.checks | Where-Object { $_.passed -ne $true }).Count -ne 0) { $reportProblems.Add('One or more source checks did not pass.') }
        foreach ($driver in $drivers) {
            if (@($source.checks | Where-Object { $_.driver -ceq $driver }).Count -eq 0) { $reportProblems.Add("Report has no $driver checks.") }
        }
        $checks.Add([pscustomobject]@{ kind = 'report'; report = $reportInfo.Path; passed = $reportProblems.Count -eq 0; message = [string]::Join(' ', $reportProblems) })
        $reportDirectory = [System.IO.Path]::GetDirectoryName($reportInfo.Path)
        $reports.Add([pscustomobject]@{ path = $reportInfo.Path; sha256 = $reportInfo.Hash; finishedAt = $source.finishedAt; completedAllScenarios = $source.completedAllScenarios; sourcePassed = $source.passed; repetitions = $repeatCount })

        foreach ($driver in $drivers) {
            for ($repetition = 1; $repetition -le $repeatCount; $repetition++) {
                foreach ($scenarioName in $scenarioNames) {
                    $context = [ordered]@{ kind = 'scenario'; report = $reportInfo.Path; driver = $driver; repetition = $repetition; scenario = $scenarioName }
                    try {
                        $matching = @($source.checks | Where-Object { $_.driver -ceq $driver -and [int]$_.repetition -eq $repetition -and $_.name -ceq $scenarioName })
                        Require ($matching.Count -eq 1) 'Expected exactly one completed check for this driver/repetition/scenario.'
                        $check = $matching[0]
                        Require ($check.passed -eq $true -and $check.actual -ceq 'passed' -and $check.expected -ceq 'passed' -and $check.readOnlyStateUnchanged -eq $true) 'Scenario did not pass its original read-only checks.'
                        Require (@($check.observerErrors).Count -eq 0) 'Scenario contains observer verification errors.'
                        $directory = [System.IO.Path]::GetFullPath([string]$check.artifactDirectory)
                        Require ($directory.StartsWith($reportDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Scenario evidence escapes its report directory.'
                        $runInfo = Read-JsonEvidence (Join-Path $directory 'run.json'); $run = $runInfo.Data
                        Require ($run.status -ceq 'passed' -and $null -ne $run.finishedAt -and $run.testName -ceq $scenarioName) 'Run is incomplete, failed, or belongs to a different scenario.'
                        $steps = @($run.steps)
                        Require ($steps.Count -eq 4 -and @($steps | Where-Object { $_.status -cne 'passed' }).Count -eq 0) 'The exact four-step read-only scenario did not complete.'
                        for ($i = 0; $i -lt $steps.Count; $i++) { Require ([int]$steps[$i].index -eq $i) 'Run step indices are incomplete or reordered.' }
                        Require ($steps[0].step.action -ceq 'click' -and $steps[0].step.selector -ceq 'id:ResetLab') 'Reset baseline action is missing.'
                        Require ($steps[1].step.action -ceq 'assertText' -and $steps[1].step.selector -ceq 'id:FixtureState' -and $steps[1].step.value -ceq 'selections=0; viewports=0; trees=0; actions=0') 'Settled zero-state baseline assertion is missing.'
                        $container = if ($scenarioName.StartsWith('Grid ', [StringComparison]::Ordinal)) { 'OrdersGrid' } else { 'OrdersList' }
                        Require ($steps[2].step.action -ceq 'assertItemExists' -and $steps[3].step.action -ceq 'assertItemAbsent') 'Expected logical existence/absence assertions are missing.'
                        for ($i = 2; $i -lt 4; $i++) {
                            Require ($steps[$i].step.selector -ceq "id:$container") 'Logical assertion targets the wrong collection.'
                            $item = $steps[$i].step.value | ConvertFrom-Json
                            $label = if ($i -eq 2) { 'Order 1999' } else { 'Order 9999' }
                            $status = if ($i -eq 2) { 'unique' } else { 'missing' }
                            Require ($item.item.label -ceq $label -and $steps[$i].itemLookup.status -ceq $status -and [DateTimeOffset]$steps[$i].itemLookup.observedAt -ge [DateTimeOffset]$steps[$i].startedAt) 'Logical lookup lacks its exact separately timestamped outcome.'
                        }
                        $baselineInfo = Read-JsonEvidence (Join-Path $directory 'step-002.json'); $baseline = $baselineInfo.Data
                        Check-Snapshot $baseline $baseline $run $baselineInfo.Path
                        Require ([DateTimeOffset]$baseline.capturedAt -eq [DateTimeOffset]$steps[1].snapshot.capturedAt) 'Baseline file differs from the recorded baseline snapshot timestamp.'
                        Require (($baseline | ConvertTo-Json -Depth 100 -Compress) -ceq ($steps[1].snapshot | ConvertTo-Json -Depth 100 -Compress)) 'Baseline file differs from the snapshot embedded in the run.'
                        $baseSets = @{}
                        foreach ($id in @('OrdersGrid', 'OrdersList')) { $baseSets[$id] = Get-RealizedFootprint $baseline $id $driver }
                        foreach ($id in @('OrdersGrid', 'OrdersList')) {
                            $farId = if ($id -ceq 'OrdersGrid') { 'Order-1999' } else { 'ListOrder-1999' }
                            Require (@($baseSets[$id].Members | Where-Object { $_.automationId -ceq $farId }).Count -eq 0) "Far item $farId was already realized at the settled baseline; this scenario cannot prove lookup of an unrealized item."
                        }
                        $afterFiles = @()
                        foreach ($step in @($steps | Where-Object { [int]$_.index -gt 1 -and $_.status -cne 'skipped' })) {
                            $afterFiles += [pscustomobject]@{ Path = Join-Path $directory ('step-{0:000}.json' -f ([int]$step.index + 1)); Step = $step }
                        }
                        $afterFiles += [pscustomobject]@{ Path = Join-Path $directory 'verification-after.json'; Step = $null }
                        foreach ($file in $afterFiles) {
                            $observedInfo = Read-JsonEvidence $file.Path; $observed = $observedInfo.Data
                            Check-Snapshot $observed $baseline $run $observedInfo.Path
                            if ($null -ne $file.Step) { Require ([DateTimeOffset]$observed.capturedAt -eq [DateTimeOffset]$file.Step.snapshot.capturedAt) 'Executed snapshot timestamp differs from the recorded step.' }
                            if ($null -ne $file.Step) { Require (($observed | ConvertTo-Json -Depth 100 -Compress) -ceq ($file.Step.snapshot | ConvertTo-Json -Depth 100 -Compress)) 'Executed snapshot differs from the snapshot embedded in the run.' }
                            foreach ($id in @('OrdersGrid', 'OrdersList')) {
                                $beforeSet = $baseSets[$id]; $afterSet = Get-RealizedFootprint $observed $id $driver
                                $added = @($afterSet.Members | Where-Object { -not $beforeSet.Identities.Contains($_.key) } | Select-Object automationId, controlType, runtimeId)
                                $removed = @($beforeSet.Members | Where-Object { -not $afterSet.Identities.Contains($_.key) } | Select-Object automationId, controlType, runtimeId)
                                $sameRoot = $beforeSet.ContainerType -ceq $afterSet.ContainerType -and $beforeSet.ContainerRuntimeId -ceq $afterSet.ContainerRuntimeId
                                $equal = $sameRoot -and $added.Count -eq 0 -and $removed.Count -eq 0
                                $comparisons++
                                $checks.Add([pscustomobject]@{
                                    kind = 'realized-footprint'; report = $reportInfo.Path; driver = $driver; repetition = $repetition; scenario = $scenarioName; container = $id
                                    baseline = $baselineInfo.Path; baselineSha256 = $baselineInfo.Hash; evidence = $observedInfo.Path; evidenceSha256 = $observedInfo.Hash
                                    baselineRows = $beforeSet.Rows; baselineCells = $beforeSet.Cells; observedRows = $afterSet.Rows; observedCells = $afterSet.Cells
                                    containerIdentityUnchanged = $sameRoot; passed = $equal; added = $added; removed = $removed
                                    message = if ($equal) { 'Exact realized AutomationId/ControlType/RuntimeId set unchanged.' } else { 'Realized identity footprint changed; these observations do not prove absence of hidden realization.' }
                                })
                            }
                        }
                        $scenarios++
                        $checks.Add([pscustomobject]($context + @{ passed = $true; message = 'Exact scenario and all required snapshot files validated.'; runSha256 = $runInfo.Hash }))
                    } catch {
                        $checks.Add([pscustomobject]($context + @{ passed = $false; message = $_.Exception.Message }))
                    }
                }
            }
        }
        $finalReport = Read-JsonEvidence $reportInfo.Path
        if ($finalReport.Hash -cne $reportInfo.Hash) { $checks.Add([pscustomobject]@{ kind = 'report'; report = $reportInfo.Path; passed = $false; message = 'Source report changed during the audit. Run again after completion.' }) }
    } catch {
        $checks.Add([pscustomobject]@{ kind = 'report'; report = $suppliedPath; passed = $false; message = $_.Exception.Message })
    }
}

$failures = @($checks | Where-Object { -not $_.passed })
$result = [ordered]@{
    mode = 'Offline audit of saved WPF snapshots; no processes launched, drivers invoked, or model calls.'
    auditedAt = [DateTimeOffset]::UtcNow
    coverage = 'Both UIA and in-process probe, each declared repetition, Grid and List logical lookup scenarios. Requested far items must be absent from the realized baseline. Baseline step-002 is compared against every later executed snapshot and verification-after for both collection subtrees, including initial grid cells. Exact AutomationId/ControlType/RuntimeId identities must remain unchanged.'
    limitation = 'This proves unchanged realized identities at the recorded observation boundaries. It cannot rule out transient realization that was created and removed between captures, or behavior that the provider did not expose.'
    passed = $ReportPaths.Count -gt 0 -and $reports.Count -eq $ReportPaths.Count -and $failures.Count -eq 0 -and $scenarios -eq $expectedScenarios -and $comparisons -eq $expectedComparisons
    reportCount = $reports.Count; expectedScenarioCount = $expectedScenarios; validatedScenarioCount = $scenarios
    expectedComparisonCount = $expectedComparisons; comparisonCount = $comparisons; checkCount = $checks.Count; failedChecks = $failures.Count
    reports = $reports.ToArray(); failures = $failures; checks = $checks.ToArray()
}
$absoluteOutput = [System.IO.Path]::GetFullPath($OutputPath)
Require (-not $seenReports.Contains($absoluteOutput)) 'Audit output cannot overwrite a source report.'
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($absoluteOutput)) | Out-Null
[System.IO.File]::WriteAllText($absoluteOutput, ($result | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ passed = $result.passed; reportCount = $result.reportCount; validatedScenarioCount = $scenarios; expectedScenarioCount = $expectedScenarios; comparisonCount = $comparisons; expectedComparisonCount = $expectedComparisons; failedChecks = $failures.Count; output = $absoluteOutput } | ConvertTo-Json -Compress
if (-not $result.passed) { exit 1 }
