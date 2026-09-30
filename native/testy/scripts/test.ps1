param(
    [string]$DotNet = 'dotnet',
    [switch]$IncludeDesktop,
    [switch]$BackendOnly,
    [string]$StudioSettings = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ($StudioSettings -and ($BackendOnly -or -not $IncludeDesktop)) { throw '-StudioSettings requires -IncludeDesktop and cannot be combined with -BackendOnly.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -DotNet $DotNet -Configuration $Configuration -BackendOnly:$BackendOnly
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$verification = Join-Path $projectRoot 'verification'
New-Item -ItemType Directory -Force -Path $verification | Out-Null
$tests = Join-Path $projectRoot "tests/Testy.Tests/bin/$Configuration/net9.0-windows/Testy.Tests.dll"
& $DotNet $tests --report (Join-Path $verification 'unit-report.json')
if ($LASTEXITCODE -ne 0) { throw 'Deterministic verification failed. See verification/unit-report.json.' }
$windowsChecks = Join-Path $projectRoot "tests/Testy.WindowsChecks/bin/$Configuration/net9.0-windows/Testy.WindowsChecks.dll"
& $DotNet $windowsChecks --capture-quality (Join-Path $verification 'capture-quality.json')
if ($LASTEXITCODE -ne 0) { throw 'Offline capture regression failed. See verification/capture-quality.json.' }
& $DotNet $windowsChecks --composite-capture (Join-Path $verification 'composite-capture.json')
if ($LASTEXITCODE -ne 0) { throw 'Composite geometry/publication regression failed. See verification/composite-capture.json.' }
$cli = Join-Path $projectRoot "src/Testy.Cli/bin/$Configuration/net9.0-windows/Testy.Cli.dll"
& $DotNet $cli verify-maintenance --artifacts (Join-Path $verification 'enterprise/maintenance')
if ($LASTEXITCODE -ne 0) { throw 'Authoring/data integrity verification failed. See verification/enterprise/maintenance.' }
if ($IncludeDesktop) {
    $desktopReportPath = Join-Path $verification 'desktop-suite-report.json'
    $desktopReport = [ordered]@{ startedAt=[DateTimeOffset]::UtcNow.ToString('o'); finishedAt=$null; passed=$false; completed=$false; checks=@() }
    function Save-DesktopReport { $desktopReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $desktopReportPath -Encoding UTF8 }
    function Invoke-DesktopCheck([string]$Name, [scriptblock]$Action) {
        $check = [ordered]@{name=$Name;passed=$false;startedAt=[DateTimeOffset]::UtcNow.ToString('o');finishedAt=$null;error=$null}
        try {
            $global:LASTEXITCODE = 0
            & $Action
            if ($LASTEXITCODE -ne 0) { throw "Verification command exited $LASTEXITCODE. See its detailed report." }
            $check.passed = $true
        }
        catch { $check.error = $_.Exception.Message; Write-Warning "$Name failed: $($check.error)" }
        finally { $check.finishedAt=[DateTimeOffset]::UtcNow.ToString('o'); $desktopReport.checks += [pscustomobject]$check; Save-DesktopReport }
    }
    Save-DesktopReport
    $resolvedDotNet = (Get-Command $DotNet -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path -Parent $resolvedDotNet
    $cli = Join-Path $projectRoot "src/Testy.Cli/bin/$Configuration/net9.0-windows/Testy.Cli.dll"
    $lab = Join-Path $projectRoot "src/Testy.TestLab/bin/$Configuration/net9.0-windows/Testy.TestLab.exe"
    Invoke-DesktopCheck 'Customer Desk UIA and probe' { & $DotNet $cli verify-lab --exe $lab --artifacts (Join-Path $verification 'desktop') }
    Invoke-DesktopCheck 'Physical Windows input' { & $DotNet $windowsChecks $lab (Join-Path $verification 'supplemental') }
    Invoke-DesktopCheck 'Native protocol with physical input' { & $DotNet $cli verify-native-protocol --exe $lab --artifacts (Join-Path $verification 'native-protocol') }
    $orders = Join-Path $projectRoot "src/Testy.OrderLab/bin/$Configuration/net9.0-windows/Testy.OrderLab.exe"
    Invoke-DesktopCheck 'Repeated cross-application functionality' { & $DotNet $cli verify-functionality --orders $orders --lab $lab --repetitions 3 --artifacts (Join-Path $verification 'functionality/desktop') }
    Invoke-DesktopCheck 'Public suite CLI' { & (Join-Path $PSScriptRoot 'verify-suite.ps1') -DotNet $DotNet -Configuration $Configuration -Artifacts (Join-Path $verification 'functionality/suite-cli') }
    $wpfLab = Join-Path $projectRoot "src/Testy.WpfLab/bin/$Configuration/net9.0-windows/Testy.WpfLab.exe"
    Invoke-DesktopCheck 'Multiple windows, popup capture and guarded coordinates' { & $DotNet $cli verify-surfaces --exe $wpfLab --artifacts (Join-Path $verification 'surfaces') }
    Invoke-DesktopCheck 'Standard and opted-in template grid editors' { & $DotNet $cli verify-grid-editors --exe $wpfLab --artifacts (Join-Path $verification 'grid-editors') }
    Invoke-DesktopCheck 'Editable WPF workflows, diagnostics and recovery' { & $DotNet $cli verify-enterprise --exe $wpfLab --repetitions 3 --artifacts (Join-Path $verification 'enterprise/desktop') }
    Invoke-DesktopCheck 'Isolated interactive workers and watchdogs' { & $DotNet $cli verify-worker --exe $lab --artifacts (Join-Path $verification 'enterprise/workers') }
    Invoke-DesktopCheck 'MCP server over stdio and HTTP' { & $DotNet $cli verify-mcp --lab $lab --artifacts (Join-Path $verification 'mcp') }
    # The recorder refuses to overwrite an earlier recording, so each run records into its own folder.
    Invoke-DesktopCheck 'Recorded demonstrations and parameterized cases' { & (Join-Path $PSScriptRoot 'verify-authoring-desktop.ps1') -DotNet $DotNet -Configuration $Configuration -Artifacts (Join-Path $verification ('enterprise/authoring/run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))) }
    Invoke-DesktopCheck 'Repeated advanced WPF controls' { & $DotNet $cli verify-wpf --exe $wpfLab --repetitions 3 --artifacts (Join-Path $verification 'wpf/desktop') }
    Invoke-DesktopCheck 'Read-only WPF realization audit' { & (Join-Path $PSScriptRoot 'verify-wpf-observations.ps1') -ReportPaths @(Join-Path $verification 'wpf/desktop/wpf-report.json') -OutputPath (Join-Path $verification 'wpf/realization-audit.json') }
    Invoke-DesktopCheck 'Hybrid WPF protocol' { & $DotNet $cli verify-wpf-hybrid --exe $wpfLab --artifacts (Join-Path $verification 'wpf/hybrid-protocol') }
    Invoke-DesktopCheck 'Text truncation boundaries' { & $DotNet $cli verify-text-boundaries --exe $wpfLab --artifacts (Join-Path $verification 'wpf/text-boundaries') }
    if (-not $BackendOnly) {
        $studio = Join-Path $projectRoot "src/Testy.Studio/bin/$Configuration/net9.0-windows/Testy.Studio.exe"
        [string[]]$studioArguments = @()
        if ($StudioSettings) { $studioArguments = @('--settings', $StudioSettings) }
        Invoke-DesktopCheck 'Studio user flows' { & $DotNet $cli verify-studio --exe $studio --lab $lab --artifacts (Join-Path $verification 'studio') @studioArguments }
        Invoke-DesktopCheck 'Studio persistence and authoring regressions' { & $DotNet $cli verify-studio-regressions --exe $studio --lab $lab --artifacts (Join-Path $verification 'studio-regressions') @studioArguments }
        Invoke-DesktopCheck 'Studio workflows, local jobs and recovery controls' { & $DotNet $cli verify-workflows --exe $studio --lab $lab --artifacts (Join-Path $verification 'studio-workflows') }
    }
    $desktopReport.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $desktopReport.completed = $true
    $desktopReport.passed = @($desktopReport.checks | Where-Object {-not $_.passed}).Count -eq 0
    Save-DesktopReport
    if (-not $desktopReport.passed) { throw "One or more desktop checks failed. All independent checks were attempted; see $desktopReportPath." }
}
