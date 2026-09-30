#requires -Version 7.5
# Offline compiled-report regression helper. Requires a PowerShell host using .NET 9 or newer.
param(
    [string]$CliAssembly = (Join-Path $PSScriptRoot '../src/Testy.Cli/bin/Release/net9.0-windows/Testy.Cli.dll'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../verification/wpf-advanced/report-terminal-guards.json')
)
$ErrorActionPreference = 'Stop'
$CliAssembly = [IO.Path]::GetFullPath($CliAssembly)
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $CliAssembly) 'Testy.Core.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($CliAssembly)
$reportType = $assembly.GetType('Testy.Cli.VerificationReport', $true)
$checkType = $assembly.GetType('Testy.Cli.VerificationCheck', $true)
function New-Report([int]$Count, [bool]$Completed) {
    $report = [Activator]::CreateInstance($reportType, $true)
    $report.PlannedChecks = 2
    $report.CompletedAllScenarios = $Completed
    for ($i = 0; $i -lt $Count; $i++) {
        $check = [Activator]::CreateInstance($checkType, $true)
        $check.Name = "Successful fixture stage $i"
        $check.Passed = $true
        $report.Checks.Add($check)
    }
    return $report
}
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) { $checks.Add([pscustomobject]@{name=$Name; passed=$Passed}) }
$report = New-Report 0 $false; $report.Finish([Threading.CancellationToken]::None)
Check 'An empty finished report cannot pass' (-not $report.Passed)
$report = New-Report 1 $false; $report.Finish([Threading.CancellationToken]::None)
Check 'A stopped report with one successful stage cannot pass' (-not $report.Passed)
$report = New-Report 1 $true; $report.Finish([Threading.CancellationToken]::None)
Check 'An incorrect completion flag cannot hide a missing planned stage' (-not $report.Passed)
$report = New-Report 2 $true
Check 'A completed but not finalized report cannot pass' (-not $report.Passed)
$report.Finish([Threading.CancellationToken]::None)
Check 'All planned successful stages pass only after finalization' $report.Passed
$report.Checks[1].Passed = $false
Check 'A failed stage prevents a completed report passing' (-not $report.Passed)
$report = New-Report 3 $true; $report.Finish([Threading.CancellationToken]::None)
Check 'An unexpected extra stage cannot pass the exact-count guard' (-not $report.Passed)
$cancellation = [Threading.CancellationTokenSource]::new()
try {
    $report = New-Report 1 $false
    $cancellation.Cancel()
    $report.Finish($cancellation.Token)
    Check 'Cancellation after partial success is terminal and cannot pass' ($report.Cancelled -and -not $report.Passed)
    $report = New-Report 2 $true
    $report.Finish($cancellation.Token)
    Check 'Cancellation during final owned-process cleanup cannot pass' ($report.Cancelled -and -not $report.Passed)
} finally { $cancellation.Dispose() }
$result = [pscustomobject]@{
    completedAt=[DateTimeOffset]::UtcNow.ToString('o')
    mode='Offline checks against the compiled CLI report; no desktop or model calls'
    cliSha256=(Get-FileHash -LiteralPath $CliAssembly -Algorithm SHA256).Hash
    passed=(@($checks | Where-Object {-not $_.passed}).Count -eq 0 -and $checks.Count -eq 9)
    checks=@($checks)
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path $OutputPath)) | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$result | ConvertTo-Json -Depth 8
if (-not $result.passed) { exit 1 }
