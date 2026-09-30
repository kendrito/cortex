param(
    [string]$DotNet = 'dotnet',
    [string]$Artifacts = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Artifacts) { $Artifacts = Join-Path $projectRoot 'verification/enterprise/authoring-desktop' }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$runtime = (Get-Command $DotNet -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path -Parent $runtime
$cli = (Resolve-Path (Join-Path $projectRoot "src/Testy.Cli/bin/$Configuration/net9.0-windows/Testy.Cli.dll")).Path
$lab = (Resolve-Path (Join-Path $projectRoot "src/Testy.TestLab/bin/$Configuration/net9.0-windows/Testy.TestLab.exe")).Path
. (Join-Path $PSScriptRoot 'screenshot-content.ps1')
$report = [ordered]@{ startedAt=[datetimeoffset]::UtcNow; finishedAt=$null; passed=$false; plannedChecks=2; mode='Real owned Customer Desk event recording and parameterized replay; no model calls'; checks=@(); error=$null }
function Save-Report { $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Artifacts 'authoring-desktop-report.json') }
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Arg([string]$value) { return '"' + ($value -replace '(\\*)"','$1$1\"' -replace '(\\+)$','$1$1') + '"' }
Save-Report
$owned = $null; $recording = $null
try {
    $owned = Start-Process -FilePath $lab -WorkingDirectory (Split-Path $lab) -WindowStyle Hidden -PassThru
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100; $owned.Refresh()
        Check (-not $owned.HasExited) 'Owned fixture exited during startup.'
        Check ([datetime]::UtcNow -lt $deadline) 'Owned fixture window did not appear.'
    } while ($owned.MainWindowHandle -eq [IntPtr]::Zero)

    $demoPath = Join-Path $Artifacts 'customer-demonstration.json'
    Check (-not (Test-Path -LiteralPath $demoPath)) 'Recording output already exists; choose a new artifacts directory.'
    [string[]]$recordArgs = @((Arg $cli),'record','--pid',([string]$owned.Id),'--output',(Arg $demoPath),'--duration-seconds','25')
    $recording = Start-Process -FilePath $runtime -ArgumentList $recordArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Artifacts 'record-stdout.json') -RedirectStandardError (Join-Path $Artifacts 'record-stderr.txt')
    $null = $recording.Handle # Windows PowerShell only reports ExitCode for a Start-Process object whose handle was opened while it ran.
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    $ready = $false
    while (-not $ready) {
        Start-Sleep -Milliseconds 100; $recording.Refresh()
        Check (-not $recording.HasExited) 'Recorder exited before subscribing to fixture events.'
        Check ([datetime]::UtcNow -lt $deadline) 'Recorder did not expose its subscription-ready output.'
        if (Test-Path -LiteralPath $demoPath) {
            try { $ready = [bool](Get-Content -LiteralPath $demoPath -Raw | ConvertFrom-Json).recordingActive } catch { $ready = $false }
        }
    }
    $steps = @(
        @{ action='click'; selector='id:ResetButton'; value=''; title='Reset demonstration fixture' },
        @{ action='typeText'; selector='id:CustomerName'; value='Ada Lovelace'; title='Enter demonstrated name' },
        @{ action='typeText'; selector='id:CustomerEmail'; value='ada@example.test'; title='Enter demonstrated email' },
        @{ action='click'; selector='id:AddCustomer'; value=''; title='Add demonstrated customer' }
    )
    $index = 0
    foreach ($step in $steps) {
        $request = Join-Path $Artifacts ('action-{0:00}.json' -f (++$index))
        @{operation='execute';step=$step} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $request
        $raw = & $runtime $cli tool --pid $owned.Id --request $request --artifacts (Join-Path $Artifacts 'demonstrated-actions')
        $exit = $LASTEXITCODE; $run = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
        Check ($exit -eq 0 -and $run.status -eq 'passed' -and $run.target.processId -eq $owned.Id) 'Demonstrated action did not pass on the owned fixture.'
        Start-Sleep -Milliseconds 700
    }
    Check ($recording.WaitForExit(40000)) 'Recorder exceeded its bounded capture period.'
    $recording.Refresh(); Check ($recording.ExitCode -eq 0) 'Recorder returned failure.'
    $demo = Get-Content -LiteralPath $demoPath -Raw | ConvertFrom-Json
    Check ($demo.completed -and -not $demo.cancelled -and $demo.droppedEvents -eq 0 -and $demo.finishedAt -and $demo.target.processId -eq $owned.Id) 'Recording is incomplete, lost events or belongs to another process.'
    $cursor = 0
    foreach ($action in $demo.actions) {
        Check ($action.step.action -in @('click','typeText')) 'Recording invented an assertion or unsupported gesture.'
        Check ($action.runtimeId -and $action.resolvedAt -ge $action.observedAt) 'Event identity or resolution timestamp is missing.'
        if ($cursor -lt $steps.Count -and $action.step.action -ceq $steps[$cursor].action -and $action.step.selector -ceq $steps[$cursor].selector -and $action.step.value -ceq $steps[$cursor].value) { $cursor++ }
    }
    Check ($cursor -eq $steps.Count) 'Recorded events did not preserve all four demonstrated actions in order.'
    foreach ($image in @($demo.initialScreenshot,$demo.finalScreenshot)) { Check (Get-FixtureScreenshotContent -Path $image).passed 'Recording screenshot lacks readable client content.' }
    $report.checks += [pscustomobject]@{ name='Observed demonstration events preserve four requested UI actions'; passed=$true; requestedActions=4; observedEvents=$demo.actions.Count; assertionsInRecording=0; demonstration=$demoPath }
    Save-Report

    $suitePath = Join-Path $Artifacts 'materialized-suite.json'
    $raw = & $runtime $cli materialize-template --template (Join-Path $projectRoot 'examples/customer-template.json') --data (Join-Path $projectRoot 'examples/customer-data.json') --output $suitePath
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $suitePath)) 'Template materialization failed.'
    $definition = Get-Content -LiteralPath $suitePath -Raw | ConvertFrom-Json
    $raw = & $runtime $cli run-suite --suite $suitePath --pid $owned.Id --replay --artifacts (Join-Path $Artifacts 'parameterized-suite')
    $exit = $LASTEXITCODE; $raw | Set-Content -LiteralPath (Join-Path $Artifacts 'materialized-suite-stdout.json'); $suite = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
    Check ($exit -eq 0 -and $suite.status -eq 'passed' -and $suite.entries.Count -eq 3 -and $suite.passed -eq 3 -and $suite.finishedAt) 'Three materialized cases did not all pass.'
    $images = 0
    for ($case = 0; $case -lt 3; $case++) {
        $run = $suite.entries[$case].run; $test = $definition.tests[$case]
        Check ($run.target.processId -eq $owned.Id -and $run.testId -ceq $test.id -and $run.steps.Count -eq 6 -and $run.finishedAt) 'Materialized run identity or step count differs.'
        for ($step = 0; $step -lt 6; $step++) {
            $actual = $run.steps[$step]; $expected = $test.steps[$step]
            Check ($actual.index -eq $step -and $actual.status -eq 'passed' -and $actual.step.id -ceq $expected.id -and $actual.step.action -ceq $expected.action -and $actual.step.selector -ceq $expected.selector -and $actual.step.value -ceq $expected.value) 'Materialized canonical step changed during execution.'
            Check ($actual.snapshot.target.processId -eq $owned.Id -and (Get-FixtureScreenshotContent -Path $actual.screenshotPath).passed) 'Materialized step lacks readable owned-target evidence.'
            $images++
        }
    }
    $report.checks += [pscustomobject]@{name='Explicit value template materializes and replays three acceptance cases';passed=$true;cases=3;steps=18;images=$images;suiteDirectory=$suite.artifactDirectory}
    $report.passed = $report.checks.Count -eq 2 -and @($report.checks | Where-Object { -not $_.passed }).Count -eq 0
} catch { $report.error=$_.Exception.Message; $report.passed=$false }
finally {
    if ($recording -and -not $recording.HasExited) { $recording.Kill(); [void]$recording.WaitForExit(3000) }
    if ($owned -and -not $owned.HasExited) { [void]$owned.CloseMainWindow(); if (-not $owned.WaitForExit(3000)) { $owned.Kill(); [void]$owned.WaitForExit(3000) } }
    $report.finishedAt=[datetimeoffset]::UtcNow; Save-Report
}
$report | ConvertTo-Json -Depth 12
if (-not $report.passed) { exit 1 }
