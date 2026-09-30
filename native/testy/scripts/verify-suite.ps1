param(
    [string]$DotNet = 'dotnet',
    [string]$Artifacts = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Artifacts) { $Artifacts = Join-Path $projectRoot 'verification/functionality/suite-cli' }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$runtime = (Get-Command $DotNet -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path -Parent $runtime
$cli = Join-Path $projectRoot "src/Testy.Cli/bin/$Configuration/net9.0-windows/Testy.Cli.dll"
$lab = (Resolve-Path (Join-Path $projectRoot "src/Testy.TestLab/bin/$Configuration/net9.0-windows/Testy.TestLab.exe")).Path
. (Join-Path $PSScriptRoot 'screenshot-content.ps1')
$report = [ordered]@{ startedAt=[datetimeoffset]::UtcNow; finishedAt=$null; passed=$false; mode='Real owned WPF fixture; explicit replay and AI-suite preflight; no model calls'; checks=@(); error=$null }
function Save-Report { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Artifacts 'suite-cli-report.json') }
function Check([bool]$condition, [string]$message) { if(-not $condition) { throw $message } }
function Check-Images($suite) {
    $count = 0
    foreach($entry in $suite.entries) {
        if($null -eq $entry.run) { continue }
        foreach($step in $entry.run.steps) {
            if($step.status -eq 'skipped') { continue }
            Check ($null -ne $step.snapshot) 'A suite step has no tree evidence.'
            $quality = Get-FixtureScreenshotContent -Path $step.screenshotPath
            Check $quality.passed 'A suite screenshot lacks readable client content.'
            $count++
        }
    }
    return $count
}
Save-Report
$owned = Start-Process -FilePath $lab -WorkingDirectory (Split-Path $lab) -WindowStyle Hidden -PassThru
try {
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100; $owned.Refresh()
        if($owned.HasExited) { throw 'The owned fixture exited during startup.' }
        if([datetime]::UtcNow -gt $deadline) { throw 'The owned fixture did not show a window.' }
    } while($owned.MainWindowHandle -eq [IntPtr]::Zero)
    $positiveRaw = & $runtime $cli run-suite --suite (Join-Path $projectRoot 'examples/customer-suite.json') --pid $owned.Id --replay --artifacts (Join-Path $Artifacts 'positive')
    $positiveExit = $LASTEXITCODE
    $positive = ($positiveRaw -join [Environment]::NewLine) | ConvertFrom-Json
    Check ($positiveExit -eq 0 -and $positive.status -eq 'passed' -and $positive.passed -eq 3 -and $positive.entries.Count -eq 3) 'Positive CLI suite did not pass exactly three planned runs.'
    $positiveImages = Check-Images $positive
    Check ($positiveImages -eq 18) 'Positive suite did not record all18 expected step screenshots.'
    foreach($file in @('suite.json','suite-definition.json','suite.html','suite-junit.xml')) { Check (Test-Path (Join-Path $positive.artifactDirectory $file)) "Missing suite artifact $file" }
    $report.checks += [pscustomobject]@{name='CLI explicit replay suite';passed=$true;exitCode=$positiveExit;runs=3;stepImages=$positiveImages;artifactDirectory=$positive.artifactDirectory}
    Save-Report

    $definitions = Join-Path $Artifacts 'definitions'; New-Item -ItemType Directory -Force -Path $definitions | Out-Null
    $first = @{ id='intentional-suite-failure'; name='First suite case intentionally fails'; steps=@(
        @{action='click';selector='id:ResetButton';title='Reset owned fixture'},
        @{action='assertText';selector='id:StatusMessage';value='This is intentionally wrong';timeoutMs=400;title='Wrong immutable expected value'}
    ) }
    $later = @{ id='suite-mutation-must-be-skipped'; name='Later mutation must never execute'; steps=@(@{action='click';selector='id:AddCustomer';title='Must be skipped'}) }
    $first | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $definitions 'first.json')
    $later | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $definitions 'later.json')
    @{name='Real suite fail-fast verification';repetitions=3;testFiles=@('first.json','later.json')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $definitions 'suite.json')
    $negativeRaw = & $runtime $cli run-suite --suite (Join-Path $definitions 'suite.json') --pid $owned.Id --replay --artifacts (Join-Path $Artifacts 'negative')
    $negativeExit = $LASTEXITCODE
    $negative = ($negativeRaw -join [Environment]::NewLine) | ConvertFrom-Json
    Check ($negativeExit -eq 1 -and $negative.status -eq 'failed' -and $negative.failed -eq 1 -and $negative.skipped -eq 5) 'Negative CLI suite did not fail once and skip allfive later planned runs.'
    Check ($negative.entries.Count -eq 6 -and $negative.entries[0].run.steps[1].step.value -eq 'This is intentionally wrong') 'Negative suite changed the saved expectation or planned count.'
    $failedSteps = $negative.entries[0].run.steps
    Check ($failedSteps.Count -eq 2 -and $failedSteps[0].status -eq 'passed' -and $failedSteps[0].step.action -eq 'click' -and $failedSteps[0].step.selector -eq 'id:ResetButton') 'The required reset did not complete successfully before the negative assertion.'
    Check ($failedSteps[1].status -eq 'failed' -and $failedSteps[1].step.action -eq 'assertText' -and $failedSteps[1].step.selector -eq 'id:StatusMessage') 'The intended wrong assertion was not the failing step.'
    Check (@($failedSteps[1].failureDiagnostics | Where-Object {$_.category -eq 'assertionMismatch'}).Count -ge 1) 'Negative suite lacks a recorded exact assertion mismatch.'
    Check (@($negative.entries | Select-Object -Skip 1 | Where-Object {$_.status -ne 'skipped' -or $null -ne $_.run}).Count -eq 0) 'A later suite test was executed after failure.'
    $negativeImages = Check-Images $negative
    Check ($negativeImages -eq 2) 'Negative suite did not preserve both reset and failed-assertion images.'
    [xml]$junit = Get-Content -Raw -LiteralPath (Join-Path $negative.artifactDirectory 'suite-junit.xml')
    Check ([int]$junit.testsuite.failures -eq 1 -and [int]$junit.testsuite.skipped -eq 5) 'JUnit did not expose the failed/skipped outcomes.'
    $postRaw = & $runtime $cli inspect --pid $owned.Id
    Check ($LASTEXITCODE -eq 0) 'Final fixture inspection failed.'
    $post = ($postRaw -join [Environment]::NewLine) | ConvertFrom-Json
    Check (($post.elements | Where-Object {$_.automationId -eq 'StatusMessage'}).value -eq 'Ready for a new customer.') 'Skipped mutation changed the actual application state.'
    Check (($post.elements | Where-Object {$_.automationId -eq 'ResultCount'}).value -eq '0 customers') 'Unexpected customer data remains after negative suite reset.'
    $postRaw | Set-Content -LiteralPath (Join-Path $Artifacts 'negative-final-tree.json')
    $report.checks += [pscustomobject]@{name='CLI fail-fast suite and unchanged real application';passed=$true;exitCode=$negativeExit;failedRuns=1;skippedRuns=5;stepImages=$negativeImages;artifactDirectory=$negative.artifactDirectory}
    Save-Report

    # The first AI case fits the budget; a later one does not. All cases must be validated
    # before creating a suite/run directory, constructing a provider, or changing the target.
    $early = @{ id='budget-valid-first'; name='Early case fits three turns'; steps=@(
        @{action='click';selector='id:AddCustomer';title='Would change validation status'},
        @{action='assertText';selector='id:StatusMessage';value='Enter a customer name.';title='Assert validation'}
    ) }
    $tooLong = @{ id='budget-invalid-later'; name='Later case requires four turns'; steps=@(
        @{action='screenshot';title='Observe fixture'},
        @{action='click';selector='id:AddCustomer';title='Would change validation status'},
        @{action='assertText';selector='id:StatusMessage';value='Enter a customer name.';title='Assert validation'}
    ) }
    $early | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $definitions 'budget-early.json')
    $tooLong | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $definitions 'budget-later.json')
    @{name='All cases must pass AI preflight';repetitions=1;testFiles=@('budget-early.json','budget-later.json')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $definitions 'budget-suite.json')
    @{kind='compatible';model='unused-preflight-model';endpoint='http://127.0.0.1:1/v1/chat/completions';apiKeyEnvironmentVariable=('TESTY_PREFLIGHT_UNUSED_' + [guid]::NewGuid().ToString('N'));aiDirectedExecution=$true;nativeComputerUse=$false;supportsImages=$false;maximumAgentTurns=3} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $definitions 'budget-settings.json')
    $preflightArtifacts = Join-Path $Artifacts 'preflight-no-runs'
    Check (-not (Test-Path -LiteralPath $preflightArtifacts)) 'Use a fresh artifacts directory for the zero-artifact preflight check.'
    $preflightRaw = & $runtime $cli run-suite --suite (Join-Path $definitions 'budget-suite.json') --pid $owned.Id --settings (Join-Path $definitions 'budget-settings.json') --artifacts $preflightArtifacts
    $preflightExit = $LASTEXITCODE
    $preflightRaw | Set-Content -LiteralPath (Join-Path $Artifacts 'preflight-result.json')
    $preflight = ($preflightRaw -join [Environment]::NewLine) | ConvertFrom-Json
    Check ($preflightExit -eq 2 -and $preflight.error -match 'requires at least 4 model turns' -and $preflight.error -match 'configured budget is 3') 'The later impossible case was not rejected by the public CLI preflight.'
    Check (-not (Test-Path -LiteralPath $preflightArtifacts)) 'AI preflight created suite/case artifacts before validating every case.'
    $afterPreflightRaw = & $runtime $cli inspect --pid $owned.Id
    Check ($LASTEXITCODE -eq 0) 'Post-preflight target inspection failed.'
    $afterPreflightRaw | Set-Content -LiteralPath (Join-Path $Artifacts 'preflight-final-tree.json')
    $afterPreflight = ($afterPreflightRaw -join [Environment]::NewLine) | ConvertFrom-Json
    foreach($id in @('StatusMessage','ResultCount','CustomerName','CustomerEmail')) {
        Check (($post.elements | Where-Object automationId -EQ $id).value -ceq ($afterPreflight.elements | Where-Object automationId -EQ $id).value) "Preflight changed fixture state at $id."
    }
    $report.checks += [pscustomobject]@{name='CLI validates every AI case before any run';passed=$true;exitCode=$preflightExit;startedRuns=0;artifactDirectoryCreated=$false;fixtureStateUnchanged=$true;provider='Unused loopback endpoint with no credential';modelCalls=0}
    $report.passed = $true
}
catch { $report.error = $_.Exception.ToString() }
finally {
    if(-not $owned.HasExited) { [void]$owned.CloseMainWindow(); if(-not $owned.WaitForExit(4000)) { $owned.Kill() } }
    $owned.Dispose(); $report.finishedAt=[datetimeoffset]::UtcNow; Save-Report
}
$report | ConvertTo-Json -Depth 10
if(-not $report.passed) { exit 1 }
