#requires -Version 5.1
<#
Runs INSIDE a Hyper-V guest as a one-shot InteractiveToken scheduled task on the signed-in desktop.
Reads run.json prepared by Testy.HyperV.ps1, moves the optional provider key from its guest-DPAPI file
into this process's environment only (deleting the file first), runs Testy.Cli.exe worker-run, and
writes exit.json. The key never appears in arguments, reports or files after this script reads it.
#>
param([Parameter(Mandatory = $true)][string]$RunDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-Json([string]$Path, $Value) { $t = $Path + '.tmp'; [IO.File]::WriteAllText($t, ($Value | ConvertTo-Json -Depth 6), $utf8); Move-Item -LiteralPath $t -Destination $Path -Force }
function Quote([string]$Value) {
    # CommandLineToArgvW quoting, identical to the worker's own target launcher.
    $builder = New-Object Text.StringBuilder; [void]$builder.Append('"'); $slashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') { [void]$builder.Append([char]92, $slashes * 2 + 1).Append('"') } else { [void]$builder.Append([char]92, $slashes).Append($ch) }
        $slashes = 0
    }
    return $builder.Append([char]92, $slashes * 2).Append('"').ToString()
}
$exit = [ordered]@{ schema = 'testy.guest-run-exit.v1'; startedAt = [DateTimeOffset]::UtcNow.ToString('o'); finishedAt = $null; exitCode = $null; error = $null
    session = (Get-Process -Id $PID).SessionId; user = [Security.Principal.WindowsIdentity]::GetCurrent().Name; keyImported = $false; keyFileDeleted = $false; keyCleared = $true; cortexRelay = $false }
$config = $null
try {
    $config = [IO.File]::ReadAllText((Join-Path $RunDirectory 'run.json')) | ConvertFrom-Json
    if ($config.PSObject.Properties['cortexRelay'] -and $config.cortexRelay) {
        if ($config.keySlot -or $config.keyFile) { throw 'Integrated guest execution refuses provider credentials.' }
        foreach ($name in @('TESTY_CORTEX_BRIDGE_URL','TESTY_CORTEX_BRIDGE_TOKEN','TESTY_CORTEX_CONTEXT')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    }
    Write-Json (Join-Path $RunDirectory 'started.json') ([ordered]@{ startedAt = $exit.startedAt; processId = $PID; session = $exit.session; user = $exit.user })
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT(x86)', 'DOTNET_HOST_PATH', 'DOTNET_SHARED_STORE', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_STARTUP_HOOKS')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    if ($config.keySlot) {
        $keyFile = [string]$config.keyFile
        $credential = Import-Clixml -LiteralPath $keyFile
        Remove-Item -LiteralPath $keyFile -Force
        $exit.keyFileDeleted = -not (Test-Path -LiteralPath $keyFile)
        if (-not $exit.keyFileDeleted) { throw 'The encrypted key file could not be deleted; the run was not started.' }
        $exit.keyCleared = $false
        [Environment]::SetEnvironmentVariable([string]$config.keySlot, $credential.GetNetworkCredential().Password, 'Process')
        $credential = $null; $exit.keyImported = $true
    }
    $arguments = @('worker-run', '--exe', [string]$config.exe, '--test', [string]$config.test)
    if ($config.settings) { $arguments += @('--settings', [string]$config.settings) } else { $arguments += '--replay' }
    if ($config.probe) { $arguments += '--probe' }
    if ($config.arguments) { $arguments += @('--target-arguments', [string]$config.arguments) }
    $arguments += @('--timeout-seconds', [string]$config.timeoutSeconds, '--startup-timeout-seconds', [string]$config.startupTimeoutSeconds, '--shutdown-grace-seconds', [string]$config.shutdownGraceSeconds,
        '--artifacts', [string]$config.artifacts, '--cancel-file', [string]$config.cancelFile)
    $start = New-Object Diagnostics.ProcessStartInfo([string]$config.cli)
    $start.Arguments = (@($arguments | ForEach-Object { Quote $_ }) -join ' ')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = Split-Path -Parent ([string]$config.cli)
    if ($config.PSObject.Properties['cortexRelay'] -and $config.cortexRelay) {
        if ($config.keySlot -or $config.keyFile) { throw 'Integrated guest execution refuses provider credentials.' }
        $start.EnvironmentVariables['TESTY_CORTEX_INTEGRATED'] = '1'
        $start.EnvironmentVariables['TESTY_CORTEX_RELAY_DIRECTORY'] = Join-Path $RunDirectory 'cortex-relay'
        foreach ($name in @('TESTY_CORTEX_BRIDGE_URL','TESTY_CORTEX_BRIDGE_TOKEN','TESTY_CORTEX_CONTEXT')) { $start.EnvironmentVariables.Remove($name) }
        $exit.cortexRelay = $true
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $RunDirectory 'worker-stdout.json'), $stdout.Result, $utf8)
    [IO.File]::WriteAllText((Join-Path $RunDirectory 'worker-stderr.txt'), $stderr.Result, $utf8)
    $exit.exitCode = $process.ExitCode
}
catch { $exit.error = ($_.Exception.Message -replace '[\r\n]+', ' ') }
finally {
    if ($config -and $config.keySlot) {
        [Environment]::SetEnvironmentVariable([string]$config.keySlot, $null, 'Process')
        $exit.keyCleared = -not [Environment]::GetEnvironmentVariable([string]$config.keySlot, 'Process')
        if ($config.keyFile -and (Test-Path -LiteralPath ([string]$config.keyFile))) { Remove-Item -LiteralPath ([string]$config.keyFile) -Force -ErrorAction SilentlyContinue }
    }
    $exit.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    Write-Json (Join-Path $RunDirectory 'exit.json') $exit
}
