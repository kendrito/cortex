param(
    [string]$DotNet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$Settings,
    [Parameter(Mandatory = $true)][string]$Test,
    [string]$Artifacts = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Artifacts) { $Artifacts = Join-Path $projectRoot 'verification/ai-live' }
$labPath = (Resolve-Path (Join-Path $projectRoot "src/Testy.TestLab/bin/$Configuration/net9.0-windows/Testy.TestLab.exe")).Path
$cliPath = (Resolve-Path (Join-Path $projectRoot "src/Testy.Cli/bin/$Configuration/net9.0-windows/Testy.Cli.dll")).Path
$resolvedDotNet = (Get-Command $DotNet -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path -Parent $resolvedDotNet
# This fixture is created for this command only. No discovered or pre-existing target is used.
$fixture = Start-Process -FilePath $labPath -WorkingDirectory (Split-Path -Parent $labPath) -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'The owned TestLab fixture exited before opening a window.' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The owned TestLab window did not appear within 20 seconds.' }
    } while ($fixture.MainWindowHandle -eq [IntPtr]::Zero)
    $rawResult = & $resolvedDotNet $cliPath run-ai --test $Test --pid $fixture.Id --settings $Settings --artifacts $Artifacts
    $resultCode = $LASTEXITCODE
    $result = ($rawResult -join [Environment]::NewLine) | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
    $summary = [pscustomobject]@{ exitCode = $resultCode; status = $result.status; summary = $result.summary; error = $result.error; artifactDirectory = $result.artifactDirectory }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Artifacts 'session-summary.json')
    $summary | ConvertTo-Json
}
finally {
    if (-not $fixture.HasExited) {
        [void]$fixture.CloseMainWindow()
        if (-not $fixture.WaitForExit(4000)) { $fixture.Kill() }
    }
    $fixture.Dispose()
}
exit $resultCode
