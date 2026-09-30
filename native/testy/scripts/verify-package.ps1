param([string]$PackageDirectory = '', [string]$VerificationDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'screenshot-content.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $projectRoot 'dist/Testy' }
if (-not $VerificationDirectory) { $VerificationDirectory = Join-Path $projectRoot 'verification' }
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$verificationPath = [System.IO.Path]::GetFullPath($VerificationDirectory)
New-Item -ItemType Directory -Force -Path $verificationPath | Out-Null
$reportPath = Join-Path $verificationPath 'package-smoke.json'
$report = [ordered]@{
    startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    finishedAt = $null
    packageDirectory = $packagePath
    status = 'running'
    passed = $false
    fixtureProcessId = $null
    checks = [System.Collections.Generic.List[object]]::new()
}
function Save-Report { $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8 }
Save-Report
$fixture = $null
$savedRuntimeRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
$savedRuntimeRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64', 'Process')
try {
    # Test the self-contained apphosts directly. No SDK or external runtime path is supplied.
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $null, 'Process')
    $cli = Join-Path $packagePath 'Testy.Cli.exe'
    $lab = Join-Path $packagePath 'Testy.TestLab.exe'
    $example = Join-Path $packagePath 'examples/create-customer.json'
    foreach ($file in @($cli, $lab, $example, (Join-Path $packagePath 'coreclr.dll'), (Join-Path $packagePath 'hostfxr.dll'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required package file is missing: $file" }
    }
    $applicationNames = @('Testy.Cli', 'Testy.TestLab', 'Testy.OrderLab', 'Testy.WpfLab')
    $studio = Join-Path $packagePath 'Testy.Studio.exe'
    if (Test-Path -LiteralPath $studio -PathType Leaf) { $applicationNames += 'Testy.Studio' }
    foreach ($name in $applicationNames) {
        $runtime = Get-Content -LiteralPath (Join-Path $packagePath "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
        if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) {
            throw "$name is not published as a self-contained app."
        }
    }
    $report.checks.Add([pscustomobject]@{ name='Self-contained runtime manifests and payload'; passed=$true })
    $help = & $cli --help
    if ($LASTEXITCODE -ne 0 -or -not (($help -join "`n") -match 'run-suite') -or -not (($help -join "`n") -match 'verify-functionality') -or -not (($help -join "`n") -match 'verify-wpf-hybrid')) { throw 'Packaged CLI help failed or lacks suite/WPF verification commands.' }
    $report.checks.Add([pscustomobject]@{ name='Packaged CLI starts without DOTNET_ROOT'; passed=$true })
    Save-Report

    # The only process this script may close is the fixture object returned here.
    $fixture = Start-Process -FilePath $lab -WorkingDirectory $packagePath -WindowStyle Hidden -PassThru
    $report.fixtureProcessId = $fixture.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'Packaged TestLab exited before opening its window.' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Packaged TestLab did not open a window within 20 seconds.' }
    } while ($fixture.MainWindowHandle -eq [IntPtr]::Zero)
    $report.checks.Add([pscustomobject]@{ name='Packaged Lab starts without DOTNET_ROOT'; passed=$true })

    foreach ($mode in @('uia', 'probe')) {
        $modeArtifacts = Join-Path $verificationPath "package-$mode"
        [string[]]$modeArguments = if ($mode -eq 'probe') { @('--probe') } else { @() }
        $rawResult = & $cli run --test $example --pid $fixture.Id --artifacts $modeArtifacts @modeArguments
        $resultCode = $LASTEXITCODE
        $run = ($rawResult -join [Environment]::NewLine) | ConvertFrom-Json
        if ($resultCode -ne 0 -or $run.status -ne 'passed') { throw "Packaged $mode run failed: $($run.summary) $($run.error)" }
        if ($run.steps.Count -ne 6) { throw "Packaged $mode example did not execute all six steps." }
        foreach ($step in $run.steps) {
            if ($step.status -ne 'passed' -or $null -eq $step.snapshot -or -not (Test-Path -LiteralPath $step.screenshotPath -PathType Leaf)) {
                throw "Packaged $mode step $($step.index) lacks successful image/tree evidence."
            }
            $stream = [System.IO.File]::OpenRead($step.screenshotPath)
            try {
                $header = [byte[]]::new(8)
                if ($stream.Read($header, 0, 8) -ne 8 -or [BitConverter]::ToString($header) -ne '89-50-4E-47-0D-0A-1A-0A') {
                    throw "Packaged $mode screenshot is not a PNG."
                }
            } finally { $stream.Dispose() }
            $content = Get-FixtureScreenshotContent -Path $step.screenshotPath
            if (-not $content.passed) {
                throw "Packaged $mode step $($step.index) lacks visible fixture client content: $($content.reason)"
            }
            $tree = Join-Path $run.artifactDirectory ('step-{0:D3}.json' -f ([int]$step.index + 1))
            if (-not (Test-Path -LiteralPath $tree -PathType Leaf)) { throw "Packaged $mode snapshot file missing: $tree" }
        }
        foreach ($file in @('run.json', 'report.html', 'junit.xml')) {
            if (-not (Test-Path -LiteralPath (Join-Path $run.artifactDirectory $file) -PathType Leaf)) { throw "Packaged $mode is missing $file." }
        }
        $report.checks.Add([pscustomobject]@{ name="Packaged $mode customer workflow and evidence"; passed=$true; steps=$run.steps.Count; summary=$run.summary; artifactDirectory=$run.artifactDirectory })
        Save-Report
    }
    $report.passed = $true
    $report.status = 'passed'
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    $report.checks.Add([pscustomobject]@{ name='Package smoke verification'; passed=$false; error=$_.Exception.Message })
}
finally {
    if ($null -ne $fixture) {
        if (-not $fixture.HasExited) {
            [void]$fixture.CloseMainWindow()
            if (-not $fixture.WaitForExit(4000)) { $fixture.Kill() }
        }
        $fixture.Dispose()
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $savedRuntimeRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $savedRuntimeRootX64, 'Process')
    $report.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Report
}
$report | ConvertTo-Json -Depth 12
if (-not $report.passed) { exit 1 }
