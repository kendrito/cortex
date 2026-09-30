param([string]$PackageDirectory = '', [string]$VerificationDirectory = '')
$ErrorActionPreference = 'Stop'
function Assert-PackageWpfRecords {
    param($Run, $Test, [int]$ExpectedProcessId)
    if (-not $Run.finishedAt -or ([DateTimeOffset]$Run.finishedAt) -lt ([DateTimeOffset]$Run.startedAt)) { throw 'Packaged run is not finalized with a valid terminal timestamp.' }
    if ($Run.target.processId -ne $ExpectedProcessId -or $Run.testId -cne $Test.id) { throw 'Packaged run belongs to another process or requested test.' }
    if ($Run.steps.Count -ne $Test.steps.Count -or $Test.steps.Count -ne 8) { throw 'Packaged run must contain exactly all eight canonical step records.' }
    for ($index = 0; $index -lt $Test.steps.Count; $index++) {
        $actual = $Run.steps[$index]; $expected = $Test.steps[$index]
        if ($actual.index -ne $index) { throw "Packaged step record $index is missing, duplicated or reordered." }
        foreach ($property in @('id','action','selector','value','timeoutMs')) {
            if ($actual.step.$property -cne $expected.$property) { throw "Packaged step $index changed canonical $property." }
        }
        if ($null -eq $actual.snapshot -or $actual.snapshot.target.processId -ne $ExpectedProcessId) { throw "Packaged step $index lacks evidence from the owned process." }
    }
}
. (Join-Path $PSScriptRoot 'screenshot-content.ps1')
$project = Split-Path -Parent $PSScriptRoot
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $project 'dist/Testy' }
if (-not $VerificationDirectory) { $VerificationDirectory = Join-Path $project 'verification/wpf/package' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$artifacts = [IO.Path]::GetFullPath($VerificationDirectory)
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$report = [ordered]@{startedAt=[DateTimeOffset]::UtcNow.ToString('o');finishedAt=$null;passed=$false;completed=$false;mode='Published self-contained WPF eight-step replay, fresh owned fixture per driver, no model calls';packageDirectory=$package;externalRuntimeRootsCleared=$false;checks=@();error=$null}
function Save-Report { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifacts 'package-wpf-report.json') -Encoding utf8 }
Save-Report
$savedRoots = @{}
try {
    foreach ($variable in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_ROOT(x86)')) {
        $savedRoots[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
        [Environment]::SetEnvironmentVariable($variable, $null, 'Process')
    }
    $report.externalRuntimeRootsCleared = $true
    $cli = Join-Path $package 'Testy.Cli.exe'
    $lab = Join-Path $package 'Testy.WpfLab.exe'
    $testPath = Join-Path $package 'examples/wpf-control-smoke.json'
    foreach ($path in @($cli,$lab,$testPath,(Join-Path $package 'coreclr.dll'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing published file: $path" }
    }
    foreach ($application in @('Testy.Cli','Testy.WpfLab')) {
        $runtime = Get-Content -LiteralPath (Join-Path $package "$application.runtimeconfig.json") -Raw | ConvertFrom-Json
        if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw "$application is not self-contained." }
    }
    $test = Get-Content -LiteralPath $testPath -Raw | ConvertFrom-Json
    if ($test.steps.Count -ne 8) { throw 'The packaged WPF example must contain exactly eight saved steps.' }
    foreach ($mode in @('uia','probe')) {
        $owned = $null
        try {
            $owned = Start-Process -FilePath $lab -WorkingDirectory $package -WindowStyle Hidden -PassThru
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 100; $owned.Refresh()
                if ($owned.HasExited) { throw 'The owned WPF fixture exited during startup.' }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'The owned WPF fixture window did not appear.' }
            } while ($owned.MainWindowHandle -eq [IntPtr]::Zero)
            [string[]]$modeArguments = @()
            if ($mode -eq 'probe') { $modeArguments = @('--probe') }
            $initialRaw = & $cli inspect --pid $owned.Id @modeArguments
            if ($LASTEXITCODE -ne 0) { throw "Packaged $mode initial inspection failed." }
            $initialRaw | Set-Content -LiteralPath (Join-Path $artifacts "$mode-initial-tree.json") -Encoding utf8
            $initial = ($initialRaw -join [Environment]::NewLine) | ConvertFrom-Json
            if ($initial.isTruncated -or @($initial.elements | Where-Object automationId -eq 'Order-1999').Count -ne 0) { throw "Packaged $mode far row was already realized, or the initial tree was incomplete." }
            $raw = & $cli run --test $testPath --pid $owned.Id --artifacts (Join-Path $artifacts $mode) @modeArguments
            $exitCode = $LASTEXITCODE
            $raw | Set-Content -LiteralPath (Join-Path $artifacts "$mode-run-output.json") -Encoding utf8
            $run = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
            if ($exitCode -ne 0 -or $run.status -ne 'passed' -or $run.steps.Count -ne 8) { throw "Packaged $mode WPF run failed: $($run.summary) $($run.error)" }
            Assert-PackageWpfRecords -Run $run -Test $test -ExpectedProcessId $owned.Id
            foreach ($step in $run.steps) {
                if ($step.status -ne 'passed' -or $null -eq $step.snapshot -or $step.snapshot.isTruncated) { throw "Packaged $mode step $($step.index) lacks complete passing evidence." }
                $image = Get-FixtureScreenshotContent -Path $step.screenshotPath
                if (-not $image.passed -or $image.width -lt 800 -or $image.height -lt 600) { throw "Packaged $mode step $($step.index) is missing a readable full fixture client." }
                $treeFile = Join-Path $run.artifactDirectory ('step-{0:D3}.json' -f ([int]$step.index + 1))
                if (-not (Test-Path -LiteralPath $treeFile -PathType Leaf)) { throw "Missing persisted step tree: $treeFile" }
            }
            foreach ($file in @('run.json','report.html','junit.xml')) {
                if (-not (Test-Path -LiteralPath (Join-Path $run.artifactDirectory $file) -PathType Leaf)) { throw "Packaged $mode WPF run lacks $file." }
            }
            $report.checks += [pscustomobject]@{driver=$mode;passed=$true;steps=8;images=8;ownedProcessId=$owned.Id;summary=$run.summary;seconds=([DateTimeOffset]$run.finishedAt-[DateTimeOffset]$run.startedAt).TotalSeconds;artifactDirectory=$run.artifactDirectory}
            Save-Report
        } finally {
            if ($null -ne $owned) {
                if (-not $owned.HasExited) { [void]$owned.CloseMainWindow(); if (-not $owned.WaitForExit(4000)) { $owned.Kill() } }
                $owned.Dispose()
            }
        }
    }
    $report.completed = $true
    $report.passed = $report.checks.Count -eq 2 -and @($report.checks | Where-Object {-not $_.passed}).Count -eq 0
} catch { $report.error = $_.Exception.Message }
finally {
    foreach ($variable in $savedRoots.Keys) { [Environment]::SetEnvironmentVariable($variable, $savedRoots[$variable], 'Process') }
    $report.finishedAt = [DateTimeOffset]::UtcNow.ToString('o'); Save-Report
}
$report | ConvertTo-Json -Depth 10
if (-not $report.passed) { exit 1 }
