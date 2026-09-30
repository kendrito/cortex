#requires -Version 5.1
<#
Testy Hyper-V bridge. Runs on the HOST in elevated Windows PowerShell 5.1, started by Testy.Cli.
Secrets arrive only on standard input as base64 UTF-8 lines (1: guest password, 2: provider key or empty).
They are never written to disk on the host, never placed in arguments, and never echoed.
Progress is written to stdout as JSON lines; the final JSON document is written to -ResultFile.
Ported from the reviewed Hyper-V lab scripts (outputs\Axiom-HyperV\Test-RunningServer2019.ps1 and Test-GuestAxiomAi.ps1; the lab predates the rename to Testy).
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Inventory', 'Check', 'Setup', 'Run')][string]$Operation,
    [Parameter(Mandatory = $true)][string]$RequestFile,
    [Parameter(Mandatory = $true)][string]$ResultFile,
    [string]$CancelFile = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$GuestRoot = 'C:\ProgramData\TestyAgent'

function Emit([string]$Stage, [string]$Message) {
    $line = [ordered]@{ event = 'stage'; stage = $Stage; message = $Message; at = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
    [Console]::Out.WriteLine($line); [Console]::Out.Flush()
}
function Save-Result($Value) {
    $json = $Value | ConvertTo-Json -Depth 12
    $temp = $ResultFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $ResultFile -Force
}
function Read-SecretLine {
    $line = [Console]::In.ReadLine()
    if ([string]::IsNullOrEmpty($line)) { return $null }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line))
}
function Test-Cancelled { return [bool]($CancelFile -and (Test-Path -LiteralPath $CancelFile)) }
function Safe-Text([string]$Text) {
    if (-not $Text) { return '' }
    $clean = ($Text -replace '[\r\n\t]+', ' ').Trim()
    if ($clean.Length -gt 800) { $clean = $clean.Substring(0, 800) + '…' }
    return $clean
}
function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Invoke-CortexModel($Exchange) {
    # Only this HOST process sees the bridge credential. Guest data cannot select URL, headers or context.
    $endpoint = [Uri]$env:TESTY_CORTEX_BRIDGE_URL
    if ($endpoint.Scheme -ne 'http' -or -not $endpoint.IsLoopback -or $endpoint.AbsolutePath -ne '/v1/chat/completions' -or $endpoint.Query -or $endpoint.UserInfo -or $endpoint.Fragment) { throw 'Invalid Cortex host bridge endpoint.' }
    if ($env:TESTY_CORTEX_BRIDGE_TOKEN.Length -lt 24 -or $env:TESTY_CORTEX_CONTEXT.Length -lt 16) { throw 'Cortex host context is unavailable.' }
    if ([string]$Exchange.id -notmatch '^[a-f0-9]{32}$' -or [Text.Encoding]::UTF8.GetByteCount([string]$Exchange.body) -gt 16777216) { throw 'Invalid bounded guest model request.' }
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler; $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler); $client.Timeout = [TimeSpan]::FromSeconds(150); $client.MaxResponseContentBufferSize = 16777216
    try {
        $client.DefaultRequestHeaders.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer', $env:TESTY_CORTEX_BRIDGE_TOKEN)
        $client.DefaultRequestHeaders.Add('X-Testy-Cortex-Context', $env:TESTY_CORTEX_CONTEXT)
        $content = New-Object Net.Http.StringContent([string]$Exchange.body, [Text.Encoding]::UTF8, 'application/json')
        try {
            $pending = $client.PostAsync($endpoint, $content)
            while (-not $pending.IsCompleted) { if (Test-Cancelled) { $client.CancelPendingRequests(); throw 'Cortex guest operation cancelled.' }; Start-Sleep -Milliseconds 100 }
            $response = $pending.GetAwaiter().GetResult()
            try { return @{ id=[string]$Exchange.id; status=[int]$response.StatusCode; body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } }
            finally { $response.Dispose() }
        } finally { $content.Dispose() }
    } catch { return @{ id=[string]$Exchange.id; status=502; body='{"error":{"message":"Cortex model relay could not complete."}}' } }
    finally { $client.Dispose(); $handler.Dispose() }
}

# ---------------------------------------------------------------------------------------------
# Guest code: runs inside the VM over PowerShell Direct as the stored guest account.
# ---------------------------------------------------------------------------------------------
$guestCode = @'
param([string]$Op, $Data, $Secret)
$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
$GuestRoot = 'C:\ProgramData\TestyAgent'
function Utf8 { New-Object Text.UTF8Encoding($false) }
function Write-Json([string]$Path, $Value) { $t = $Path + '.tmp'; [IO.File]::WriteAllText($t, ($Value | ConvertTo-Json -Depth 8), (Utf8)); Move-Item -LiteralPath $t -Destination $Path -Force }
function Get-TasksAtPath([string]$TaskPath) {
    try { return @(Get-ScheduledTask -TaskPath $TaskPath -ErrorAction Stop) }
    catch {
        # ScheduledTasks treats an empty or missing exact task folder as NotFound; any other failure propagates.
        if ($_.FullyQualifiedErrorId -ceq 'CmdletizationQuery_NotFound_TaskPath,Get-ScheduledTask' -and $_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::ObjectNotFound) { return @() }
        throw
    }
}
function Sid-Of([string]$Account) { if ($Account.StartsWith('.\')) { $Account = $env:COMPUTERNAME + $Account.Substring(1) }; return (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier]).Value }
function Private-Directory([string]$Path, [string]$UserSid, [bool]$UsersRead) {
    $acl = New-Object Security.AccessControl.DirectorySecurity; $acl.SetAccessRuleProtection($true, $false)
    $sids = @('S-1-5-18', 'S-1-5-32-544'); if ($UserSid) { $sids += $UserSid }
    foreach ($sid in $sids) { [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }
    if ($UsersRead) { [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }
    if (Test-Path -LiteralPath $Path) { Set-Acl -LiteralPath $Path -AclObject $acl } else { [void][IO.Directory]::CreateDirectory($Path, $acl) }
}
function No-Reparse([string]$Path) {
    for ($p = $Path; $p; $p = Split-Path -Parent $p) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point rejected: $p" }
    }
}
function Desktop-State([string]$ExpectedSid) {
    if (-not ('TestyGuestWts' -as [type])) {
        Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class TestyGuestWts {
 [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
 [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WTSQuerySessionInformationW(IntPtr h,int session,int info,out IntPtr buffer,out int bytes);
 [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
 public static string Text(int session,int info){IntPtr b;int n;if(!WTSQuerySessionInformationW(IntPtr.Zero,session,info,out b,out n))return null;try{return Marshal.PtrToStringUni(b);}finally{WTSFreeMemory(b);}}
 public static int Int(int session,int info){IntPtr b;int n;if(!WTSQuerySessionInformationW(IntPtr.Zero,session,info,out b,out n))return -1;try{if(n<4)return -1;return Marshal.ReadInt32(b);}finally{WTSFreeMemory(b);}}
 public static int LockFlags(int session){IntPtr b;int n;if(!WTSQuerySessionInformationW(IntPtr.Zero,session,25,out b,out n))return -99;try{if(n<20)return -99;if(Marshal.ReadInt32(b,0)!=1||Marshal.ReadInt32(b,8)!=session)return -98;return Marshal.ReadInt32(b,16);}finally{WTSFreeMemory(b);}}
}
"@
    }
    $raw = [TestyGuestWts]::WTSGetActiveConsoleSessionId()
    if ($raw -eq [uint32]::MaxValue -or $raw -eq 0) { return [ordered]@{ ready = $false; reason = 'No active signed-in console session in the VM. Sign in through VM Connect (basic session).'; session = [int]0; user = ''; unlocked = $false } }
    $session = [int]$raw
    $user = [TestyGuestWts]::Text($session, 5); $domain = [TestyGuestWts]::Text($session, 7)
    $connect = [TestyGuestWts]::Int($session, 8); $lock = [TestyGuestWts]::LockFlags($session)
    $logonUi = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }).Count
    $explorer = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { $_.SessionId -eq $session } | ForEach-Object {
            $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner; if ($owner.ReturnValue -eq 0) { "$($owner.Domain)\$($owner.User)" } })
    $account = if ($user) { "$domain\$user" } else { '' }
    $sid = ''; if ($user) { try { $sid = Sid-Of $account } catch { $sid = '' } }
    $unlocked = ($connect -eq 0 -and $lock -eq 1 -and $logonUi -eq 0)
    $reason = if (-not $user) { 'No user is signed in to the VM console.' }
        elseif ($connect -ne 0) { 'The VM console session is disconnected. Reconnect with VM Connect in basic session mode.' }
        elseif (-not $unlocked) { 'The VM desktop is locked. Unlock it in VM Connect.' }
        elseif (@($explorer | Where-Object { $_ -ieq $account }).Count -lt 1) { 'The VM desktop shell (Explorer) is not running for the signed-in user.' }
        elseif ($ExpectedSid -and $sid -cne $ExpectedSid) { "The VM desktop is signed in as $account, not the account stored for Testy." }
        else { '' }
    return [ordered]@{ ready = ($reason -eq ''); reason = $reason; session = $session; user = $account; userSid = $sid; unlocked = $unlocked; connectState = $connect; lockFlags = $lock; logonUi = $logonUi }
}
function Inventory([string]$Root, [string[]]$Exclude) {
    $files = New-Object 'Collections.Generic.List[object]'; $pending = New-Object 'Collections.Generic.Queue[string]'; $pending.Enqueue($Root); $bytes = 0L
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Evidence contains a reparse point.' }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName); continue }
            $relative = $item.FullName.Substring($Root.Length + 1)
            if ($Exclude -contains $relative) { continue }
            $bytes += $item.Length
            if ($files.Count -ge 20000 -or $bytes -gt 2GB) { throw 'Evidence exceeds its bound (20,000 files / 2 GiB).' }
            $files.Add([ordered]@{ path = $relative.Replace('\', '/'); bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash })
        }
    }
    return @($files.ToArray())
}
$expectedSid = ''
if ($Data -and $Data.userName) { try { $expectedSid = Sid-Of ([string]$Data.userName) } catch { throw "The stored guest account '$($Data.userName)' does not exist in this VM." } }
switch ($Op) {
    'Check' {
        $params = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters' -ErrorAction SilentlyContinue
        $guestVm = if ($params -and $params.PSObject.Properties['VirtualMachineId']) { [string]$params.VirtualMachineId } else { '' }
        $os = Get-CimInstance Win32_OperatingSystem
        $desktop = Desktop-State $expectedSid
        $runTasks = @(Get-TasksAtPath '\Testy\' | Where-Object { $_.TaskName -like 'Run-*' })
        $active = @($runTasks | Where-Object { [string]$_.State -in @('Running', 'Queued') })
        $marker = Join-Path $GuestRoot ('Worker\' + $Data.workerKey + '\worker.json')
        $workerSha = ''; if ($Data.workerKey -and (Test-Path -LiteralPath $marker)) { try { $workerSha = [string](([IO.File]::ReadAllText($marker) | ConvertFrom-Json).manifestSha256) } catch { $workerSha = '' } }
        $free = 0.0; try { $free = [Math]::Round(((Get-PSDrive -Name C).Free / 1GB), 1) } catch { }
        return [pscustomobject]@{
            guestVirtualMachineId = $guestVm; computerName = $env:COMPUTERNAME; os = [string]$os.Caption; build = [string]$os.BuildNumber
            desktop = [pscustomobject]$desktop; activeRunTasks = $active.Count; staleRunTasks = @($runTasks | Where-Object { [string]$_.State -notin @('Running', 'Queued') }).Count
            workerManifestSha256 = $workerSha; freeDiskGb = $free; expectedUserSid = $expectedSid
        }
    }
    'EnsureFolders' {
        No-Reparse $GuestRoot
        Private-Directory $GuestRoot '' $true
        Private-Directory (Join-Path $GuestRoot 'Worker') '' $true
        Private-Directory (Join-Path $GuestRoot 'Runs') '' $false
        Write-Json (Join-Path $GuestRoot 'machine.json') ([ordered]@{ schema = 'testy.guest-machine.v1'; vmId = [string]$Data.vmId; host = [string]$Data.hostName; updatedAt = [DateTimeOffset]::UtcNow.ToString('o') })
        # Leftovers from an interrupted host: finished run tasks and any encrypted key file.
        $removed = 0
        foreach ($task in @(Get-TasksAtPath '\Testy\' | Where-Object { $_.TaskName -like 'Run-*' -and [string]$_.State -notin @('Running', 'Queued') })) { Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath '\Testy\' -Confirm:$false; $removed++ }
        $keys = 0
        foreach ($key in @(Get-ChildItem -LiteralPath (Join-Path $GuestRoot 'Runs') -Filter 'key.clixml' -Recurse -File -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $key.FullName -Force; $keys++ }
        return [pscustomobject]@{ ready = $true; staleTasksRemoved = $removed; staleKeysRemoved = $keys }
    }
    'InstallWorker' {
        $workerRoot = Join-Path $GuestRoot 'Worker'; No-Reparse $workerRoot
        $zip = Join-Path $workerRoot ($Data.workerKey + '.zip.part')
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -cne $Data.workerZipSha256) { throw 'Worker package transfer hash differs.' }
        $staging = Join-Path $workerRoot ($Data.workerKey + '.staging'); $final = Join-Path $workerRoot $Data.workerKey
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)
        $expected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in @($Data.manifest)) {
            $relative = ([string]$item.path).Replace('/', '\')
            if ($relative -match '(^\\|:|(^|\\)\.\.?(\\|$))') { throw 'Invalid worker manifest path.' }
            [void]$expected.Add($relative)
            $path = Join-Path $staging $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Worker file missing after transfer: $relative" }
            if ((Get-Item -LiteralPath $path).Length -ne [long]$item.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne [string]$item.sha256) { throw "Worker file differs from the sealed manifest: $relative" }
        }
        [void]$expected.Add('release-manifest.json')
        foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File -Force) { if (-not $expected.Contains($file.FullName.Substring($staging.Length + 1))) { throw 'Unexpected file in the transferred worker package.' } }
        if ((Get-FileHash -LiteralPath (Join-Path $staging 'release-manifest.json') -Algorithm SHA256).Hash -cne $Data.manifestSha256) { throw 'Worker manifest hash differs.' }
        Write-Json (Join-Path $staging 'worker.json') ([ordered]@{ schema = 'testy.guest-worker.v1'; manifestSha256 = [string]$Data.manifestSha256; version = [string]$Data.version; installedAt = [DateTimeOffset]::UtcNow.ToString('o') })
        if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Recurse -Force }
        Rename-Item -LiteralPath $staging -NewName $Data.workerKey
        Remove-Item -LiteralPath $zip -Force
        # Keep the new worker and the newest previous one; remove older copies no running process uses.
        $pruned = 0
        foreach ($old in @(Get-ChildItem -LiteralPath $workerRoot -Directory -Force | Where-Object { $_.Name -ne $Data.workerKey } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 1)) {
            $inUse = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($old.FullName + '\', [StringComparison]::OrdinalIgnoreCase) }).Count
            if ($inUse -eq 0) { try { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop; $pruned++ } catch { } }
        }
        return [pscustomobject]@{ installed = $true; files = $expected.Count; directory = $final; olderWorkersRemoved = $pruned }
    }
    'Stage' {
        $desktop = Desktop-State $expectedSid
        if (-not $desktop.ready) { throw $desktop.reason }
        $active = @(Get-TasksAtPath '\Testy\' | Where-Object { $_.TaskName -like 'Run-*' -and [string]$_.State -in @('Running', 'Queued') })
        if ($active.Count -gt 0) { throw 'Another Testy run is active in this VM; no concurrent input is allowed.' }
        $workerDir = Join-Path $GuestRoot ('Worker\' + $Data.workerKey)
        $cli = Join-Path $workerDir 'Testy.Cli.exe'; $guestRun = Join-Path $workerDir 'hyperv\guest-run.ps1'
        if (-not (Test-Path -LiteralPath $cli) -or -not (Test-Path -LiteralPath $guestRun)) { throw 'The staged Testy worker is incomplete.' }
        $runDir = Join-Path $GuestRoot ('Runs\' + $Data.runId); No-Reparse $runDir
        if (Test-Path -LiteralPath $runDir) { throw 'Run directory is not fresh.' }
        Private-Directory $runDir $expectedSid $false
        $utf8 = Utf8
        $testPath = Join-Path $runDir 'test.json'; [IO.File]::WriteAllText($testPath, [string]$Data.testJson, $utf8)
        $settingsPath = $null; if ($Data.settingsJson) { $settingsPath = Join-Path $runDir 'settings.json'; [IO.File]::WriteAllText($settingsPath, [string]$Data.settingsJson, $utf8) }
        $argumentsPath = Join-Path $runDir 'arguments.json'; [IO.File]::WriteAllText($argumentsPath, [string]$Data.argumentsJson, $utf8)
        if ($Data.stageZipName) {
            $stageZip = Join-Path (Join-Path $GuestRoot 'Runs') ([string]$Data.stageZipName)
            if ((Get-FileHash -LiteralPath $stageZip -Algorithm SHA256).Hash -cne $Data.stageZipSha256) { throw 'Copied app folder transfer hash differs.' }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($stageZip, (Join-Path $runDir 'app')); Remove-Item -LiteralPath $stageZip -Force
        }
        $exe = [string]$Data.executable
        if ($exe.StartsWith('{worker}\')) { $exe = Join-Path $workerDir $exe.Substring(9) }
        elseif ($Data.stageZipName) { $exe = Join-Path (Join-Path $runDir 'app') $exe }
        $exe = [IO.Path]::GetFullPath($exe)
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "The target executable does not exist in the VM: $exe" }
        $keyFile = $null
        if ($Secret) {
            # Export-Clixml protects the PSCredential with this guest account's DPAPI key; guest-run.ps1 deletes it on read.
            $keyFile = Join-Path $runDir 'key.clixml'
            New-Object Management.Automation.PSCredential('TESTY', $Secret) | Export-Clixml -LiteralPath $keyFile -NoClobber
        }
        $config = [ordered]@{ schema = 'testy.guest-run.v1'; runId = [string]$Data.runId; cli = $cli; exe = $exe; test = $testPath; settings = $settingsPath; arguments = $argumentsPath
            probe = [bool]$Data.probe; timeoutSeconds = [int]$Data.timeoutSeconds; startupTimeoutSeconds = [int]$Data.startupTimeoutSeconds; shutdownGraceSeconds = [int]$Data.shutdownGraceSeconds
            artifacts = (Join-Path $runDir 'artifacts'); cancelFile = (Join-Path $runDir 'cancel.request'); keySlot = $(if ($Secret) { [string]$Data.keySlot } else { $null }); keyFile = $keyFile; cortexRelay = [bool]$Data.cortexRelay }
        if ($config.cortexRelay) { Private-Directory (Join-Path $runDir 'cortex-relay') $expectedSid $false }
        Write-Json (Join-Path $runDir 'run.json') $config
        $taskName = 'Run-' + $Data.runId
        $argumentsText = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $guestRun + '" -RunDirectory "' + $runDir + '"'
        $action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $argumentsText -WorkingDirectory $runDir
        $principalAccount = (New-Object Security.Principal.SecurityIdentifier($expectedSid)).Translate([Security.Principal.NTAccount]).Value
        $principal = New-ScheduledTaskPrincipal -UserId $principalAccount -LogonType Interactive -RunLevel Limited
        $limit = New-TimeSpan -Seconds ([int]$Data.timeoutSeconds + [int]$Data.startupTimeoutSeconds + 900)
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit $limit -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $null = Register-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' -Action $action -Principal $principal -Settings $settings -Description ('Testy run ' + $Data.runId)
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\Testy\'
        if ([string]$task.Principal.LogonType -ne 'Interactive' -or @($task.Actions).Count -ne 1) { throw 'Registered run task has an unexpected principal or action.' }
        Start-ScheduledTask -TaskName $taskName -TaskPath '\Testy\'
        $started = Join-Path $runDir 'started.json'; $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $started)) {
            if ($timer.Elapsed.TotalSeconds -gt 60) { throw 'The guest run task did not start within 60 seconds.' }
            Start-Sleep -Milliseconds 250
        }
        return [pscustomobject]@{ taskName = $taskName; runDirectory = $runDir; executable = $exe; cli = $cli; startedAt = [DateTimeOffset]::UtcNow.ToString('o') }
    }
    'Poll' {
        $runDir = Join-Path $GuestRoot ('Runs\' + $Data.runId); $exitPath = Join-Path $runDir 'exit.json'
        if ($Data.cancel -and -not (Test-Path -LiteralPath (Join-Path $runDir 'cancel.request'))) { [IO.File]::WriteAllText((Join-Path $runDir 'cancel.request'), '{"cancel":true}', (Utf8)) }
        $timer = [Diagnostics.Stopwatch]::StartNew(); $state = ''
        while ($true) {
            if (Test-Path -LiteralPath $exitPath) { return [pscustomobject]@{ exited = $true; exit = ([IO.File]::ReadAllText($exitPath) | ConvertFrom-Json); taskState = 'Exited' } }
            $requestPath = Join-Path $runDir 'cortex-relay\request.json'
            if ($Data.cortexRelay -and (Test-Path -LiteralPath $requestPath)) {
                No-Reparse $requestPath
                if ((Get-Item -LiteralPath $requestPath).Length -gt 33554432) { throw 'Guest model envelope exceeds its bound.' }
                return [pscustomobject]@{ exited=$false; exit=$null; taskState='Running'; relay=([IO.File]::ReadAllText($requestPath) | ConvertFrom-Json) }
            }
            $task = Get-ScheduledTask -TaskName ('Run-' + $Data.runId) -TaskPath '\Testy\' -ErrorAction SilentlyContinue
            $state = if ($task) { [string]$task.State } else { 'Missing' }
            if ($state -notin @('Running', 'Queued')) {
                Start-Sleep -Seconds 2
                if (Test-Path -LiteralPath $exitPath) { continue }
                return [pscustomobject]@{ exited = $false; exit = $null; taskState = $state }
            }
            if ($timer.Elapsed.TotalSeconds -ge [int]$Data.waitSeconds) { return [pscustomobject]@{ exited = $false; exit = $null; taskState = $state } }
            Start-Sleep -Milliseconds 500
        }
    }
    'RelayReply' {
        $relay = Join-Path $GuestRoot ('Runs\' + $Data.runId + '\cortex-relay'); No-Reparse $relay
        $requestPath = Join-Path $relay 'request.json'; No-Reparse $requestPath
        if (-not (Test-Path -LiteralPath $requestPath)) { return @{ delivered=$false } }
        $pending = [IO.File]::ReadAllText($requestPath) | ConvertFrom-Json
        if ([string]$pending.id -cne [string]$Data.response.id -or [string]$pending.id -notmatch '^[a-f0-9]{32}$') { throw 'Model relay response identity mismatch.' }
        if ([Text.Encoding]::UTF8.GetByteCount([string]$Data.response.body) -gt 16777216) { throw 'Guest model response exceeds its bound.' }
        # Remove request before publishing response, allowing the next sequential turn to start cleanly.
        Remove-Item -LiteralPath $requestPath -Force
        Write-Json (Join-Path $relay 'response.json') $Data.response
        return @{ delivered=$true }
    }
    'StopRun' {
        $runDir = Join-Path $GuestRoot ('Runs\' + $Data.runId); $taskName = 'Run-' + $Data.runId
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' -ErrorAction SilentlyContinue
        if ($task -and [string]$task.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' }
        $stopped = 0
        foreach ($process in @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($runDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })) {
            try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop; $stopped++ } catch { }
        }
        Start-Sleep -Seconds 2
        $remaining = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($runDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
        return [pscustomobject]@{ stopped = $stopped; remaining = $remaining }
    }
    'PackEvidence' {
        $runDir = Join-Path $GuestRoot ('Runs\' + $Data.runId); No-Reparse $runDir
        $keyFile = Join-Path $runDir 'key.clixml'; $keyWasPresent = Test-Path -LiteralPath $keyFile
        if ($keyWasPresent) { Remove-Item -LiteralPath $keyFile -Force }
        $files = Inventory $runDir @('key.clixml')
        $zip = Join-Path $GuestRoot ('Runs\' + $Data.runId + '.evidence.zip')
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($runDir, $zip, [IO.Compression.CompressionLevel]::Fastest, $false)
        return [pscustomobject]@{ files = $files; zip = $zip; zipSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash; zipBytes = (Get-Item -LiteralPath $zip).Length; keyFilePresentAtCollection = $keyWasPresent }
    }
    'Cleanup' {
        $runDir = Join-Path $GuestRoot ('Runs\' + $Data.runId); $taskName = 'Run-' + $Data.runId
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' -ErrorAction SilentlyContinue
        if ($task) {
            if ([string]$task.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -TaskPath '\Testy\'; Start-Sleep -Seconds 1 }
            Unregister-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' -Confirm:$false
        }
        $keyFile = Join-Path $runDir 'key.clixml'; if (Test-Path -LiteralPath $keyFile) { Remove-Item -LiteralPath $keyFile -Force }
        $zip = Join-Path $GuestRoot ('Runs\' + $Data.runId + '.evidence.zip'); if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($runDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
        $runRemoved = $false
        if ($Data.removeRunDirectory -and $processes -eq 0 -and (Test-Path -LiteralPath $runDir)) { Remove-Item -LiteralPath $runDir -Recurse -Force; $runRemoved = -not (Test-Path -LiteralPath $runDir) }
        return [pscustomobject]@{
            taskRemoved = -not [bool](Get-ScheduledTask -TaskName $taskName -TaskPath '\Testy\' -ErrorAction SilentlyContinue)
            keyAbsent = -not (Test-Path -LiteralPath $keyFile); runProcesses = $processes; runDirectoryRemoved = $runRemoved
        }
    }
    default { throw 'Unknown guest operation.' }
}
'@

$directJob = {
    param([Guid]$VmId, [Management.Automation.PSCredential]$Credential, [string]$GuestCode, [string]$Op, $Data, $Secret, $CopyTo, $CopyFrom)
    $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; $session = $null
    try {
        $session = New-PSSession -VMId $VmId -Credential $Credential -ErrorAction Stop
        foreach ($pair in @($CopyTo)) { if ($pair) { Copy-Item -LiteralPath $pair.source -Destination $pair.destination -ToSession $session -Force -ErrorAction Stop } }
        $value = Invoke-Command -Session $session -ScriptBlock ([scriptblock]::Create($GuestCode)) -ArgumentList $Op, $Data, $Secret -ErrorAction Stop
        foreach ($pair in @($CopyFrom)) { if ($pair) { Copy-Item -LiteralPath $pair.source -Destination $pair.destination -FromSession $session -Force -ErrorAction Stop } }
        return $value
    }
    finally { if ($session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue } }
}
function Invoke-Guest([string]$Op, [hashtable]$Data, [int]$Seconds, $Secret = $null, $CopyTo = $null, $CopyFrom = $null) {
    $job = Start-Job -ScriptBlock $directJob -ArgumentList $script:vmId, $script:credential, $guestCode, $Op, $Data, $Secret, $CopyTo, $CopyFrom
    try {
        if (-not (Wait-Job -Job $job -Timeout $Seconds)) { throw "The VM did not finish '$Op' within $Seconds seconds (PowerShell Direct)." }
        if ($job.State -ne 'Completed') {
            $reason = 'unknown failure'
            try { $null = Receive-Job -Job $job -ErrorAction Stop } catch { $reason = Safe-Text $_.Exception.Message }
            throw "VM operation '$Op' failed: $reason"
        }
        $value = @(Receive-Job -Job $job -ErrorAction Stop)
        if ($value.Count -ne 1) { throw "VM operation '$Op' returned $($value.Count) results." }
        return $value[0]
    }
    finally {
        if ($job.State -in @('Running', 'NotStarted', 'Blocked')) { Stop-Job -Job $job -ErrorAction SilentlyContinue }
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
}
function Get-Vm-Checked {
    $vm = Get-VM -Id $script:vmId -ErrorAction Stop
    if ([string]$vm.State -ne 'Running') { throw "VM '$($vm.Name)' is $($vm.State); start it and sign in first." }
    return $vm
}
function Readiness($Check, $Vm) {
    $reasons = @()
    if (-not $Check.guestVirtualMachineId -or [Guid]$Check.guestVirtualMachineId -ne $script:vmId) { $reasons += 'The guest does not report this VM ID (Hyper-V data exchange); refusing to send input to an unverified machine.' }
    if (-not $Check.desktop.ready) { $reasons += [string]$Check.desktop.reason }
    if ([int]$Check.activeRunTasks -gt 0) { $reasons += 'Another Testy run is active in this VM.' }
    if ([double]$Check.freeDiskGb -gt 0 -and [double]$Check.freeDiskGb -lt 1) { $reasons += 'The VM has less than 1 GB free on C:.' }
    return [ordered]@{ checked = $true; checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); ready = ($reasons.Count -eq 0); reason = $(if ($reasons.Count) { $reasons -join ' ' } else { 'Ready: signed in and unlocked.' })
        identityMatches = ($Check.guestVirtualMachineId -and [Guid]$Check.guestVirtualMachineId -eq $script:vmId); computerName = [string]$Check.computerName
        desktopUser = [string]$Check.desktop.user; desktopUnlocked = [bool]$Check.desktop.unlocked; activeRunTasks = [int]$Check.activeRunTasks
        workerManifestSha256 = [string]$Check.workerManifestSha256; workerCurrent = ([string]$Check.workerManifestSha256 -and [string]$Check.workerManifestSha256 -ceq [string]$script:request.manifestSha256)
        freeDiskGb = [double]$Check.freeDiskGb; guestOs = [string]$Check.os; heartbeat = [string]$Vm.Heartbeat }
}

$result = [ordered]@{ schema = 'testy.hyperv-bridge.v1'; operation = $Operation; completed = $false; status = 'failed'; stage = 'start'; message = ''; startedAt = [DateTimeOffset]::UtcNow.ToString('o'); finishedAt = $null }
$script:credential = $null; $providerKey = $null
try {
    $script:request = [IO.File]::ReadAllText($RequestFile) | ConvertFrom-Json
    if ($Operation -eq 'Inventory') {
        $machines = @()
        foreach ($vm in @(Get-VM)) {
            $ips = @(); try { $ips = @((Get-VMNetworkAdapter -VM $vm -ErrorAction Stop).IPAddresses | Where-Object { $_ -and $_ -notmatch ':' }) } catch { }
            $os = ''
            try {
                $system = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName Msvm_ComputerSystem -Filter ("Name='" + $vm.Id.ToString().ToUpperInvariant() + "'")
                $kvp = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_KvpExchangeComponent -ErrorAction Stop
                foreach ($item in @($kvp.GuestIntrinsicExchangeItems)) {
                    $xml = [xml]$item
                    $name = ($xml.INSTANCE.PROPERTY | Where-Object { $_.NAME -eq 'Name' }).VALUE
                    if ($name -eq 'OSName') { $os = [string](($xml.INSTANCE.PROPERTY | Where-Object { $_.NAME -eq 'Data' }).VALUE) }
                }
            } catch { }
            $machines += [ordered]@{ vmId = $vm.Id.ToString(); name = $vm.Name; state = [string]$vm.State; heartbeat = [string]$vm.Heartbeat; uptimeSeconds = [long]$vm.Uptime.TotalSeconds
                generation = [int]$vm.Generation; ipAddresses = @($ips); guestOs = $os }
        }
        $result.machines = @($machines); $result.status = 'ok'; $result.completed = $true
        return
    }
    $script:vmId = [Guid]$script:request.vmId
    $password = Read-SecretLine
    if (-not $password) { throw 'No guest credential was supplied.' }
    $script:credential = New-Object Management.Automation.PSCredential([string]$script:request.userName, (ConvertTo-SecureString $password -AsPlainText -Force))
    $password = $null
    $secondLine = Read-SecretLine
    if ($secondLine) { $providerKey = ConvertTo-SecureString $secondLine -AsPlainText -Force }
    $secondLine = $null
    $vm = Get-Vm-Checked
    $result.vmName = $vm.Name
    $identity = @{ userName = [string]$script:request.userName; workerKey = [string]$script:request.workerKey }

    $result.stage = 'check'; Emit 'check' "Checking '$($vm.Name)' over PowerShell Direct."
    $check = Invoke-Guest 'Check' $identity 120
    $result.readiness = Readiness $check $vm
    if ($Operation -eq 'Check') { $result.status = 'ok'; $result.completed = $true; return }

    if ($Operation -eq 'Setup' -or $Operation -eq 'Run') {
        if (-not $result.readiness.identityMatches) { $result.status = 'unavailable'; $result.message = $result.readiness.reason; $result.completed = $true; return }
        $result.stage = 'folders'; Emit 'folders' 'Preparing C:\ProgramData\TestyAgent in the VM.'
        $result.folders = Invoke-Guest 'EnsureFolders' @{ vmId = $script:vmId.ToString(); hostName = $env:COMPUTERNAME; userName = [string]$script:request.userName } 120
        if (-not $result.readiness.workerCurrent) {
            $result.stage = 'worker'; Emit 'worker' 'Copying the Testy worker into the VM (first run for this version).'
            $workerData = @{ userName = [string]$script:request.userName; workerKey = [string]$script:request.workerKey; workerZipSha256 = [string]$script:request.workerZipSha256
                manifest = @($script:request.manifest); manifestSha256 = [string]$script:request.manifestSha256; version = [string]$script:request.version }
            $copy = @(@{ source = [string]$script:request.workerZip; destination = ($GuestRoot + '\Worker\' + $script:request.workerKey + '.zip.part') })
            $result.worker = Invoke-Guest 'InstallWorker' $workerData 1800 $null $copy
            $result.readiness.workerCurrent = $true; $result.readiness.workerManifestSha256 = [string]$script:request.manifestSha256
        }
        if ($Operation -eq 'Setup') { $result.status = 'ok'; $result.completed = $true; return }
    }

    # ---- Run -----------------------------------------------------------------------------------
    if (-not $result.readiness.ready) { $result.status = 'unavailable'; $result.message = $result.readiness.reason; $result.completed = $true; return }
    if (Test-Cancelled) { $result.status = 'cancelled'; $result.message = 'Cancelled before anything was started in the VM.'; $result.completed = $true; return }
    $runId = [string]$script:request.runId
    $runData = @{ userName = [string]$script:request.userName; workerKey = [string]$script:request.workerKey; runId = $runId; testJson = [string]$script:request.testJson
        settingsJson = $script:request.settingsJson; argumentsJson = [string]$script:request.argumentsJson; executable = [string]$script:request.executable; probe = [bool]$script:request.probe
        timeoutSeconds = [int]$script:request.timeoutSeconds; startupTimeoutSeconds = [int]$script:request.startupTimeoutSeconds; shutdownGraceSeconds = [int]$script:request.shutdownGraceSeconds
        keySlot = [string]$script:request.keySlot; cortexRelay = [bool]$script:request.cortexRelay; stageZipName = $null; stageZipSha256 = $null }
    $stageCopy = $null
    if ($script:request.stageZip) {
        $runData.stageZipName = $runId + '.app.zip'; $runData.stageZipSha256 = [string]$script:request.stageZipSha256
        $stageCopy = @(@{ source = [string]$script:request.stageZip; destination = ($GuestRoot + '\Runs\' + $runData.stageZipName) })
    }
    $result.stage = 'stage'; Emit 'stage' 'Staging the test and starting it on the VM desktop.'
    $result.staged = $false
    try { $result.run = Invoke-Guest 'Stage' $runData 600 $providerKey $stageCopy; $result.staged = $true }
    finally { if ($providerKey) { $providerKey.Dispose(); $providerKey = $null } }

    $result.stage = 'running'; Emit 'running' 'The test is running inside the VM.'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([int]$script:request.timeoutSeconds + [int]$script:request.startupTimeoutSeconds + 300)
    $cancelForwardedAt = $null; $exit = $null; $taskState = ''
    while ($true) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { $result.hostDeadlineExceeded = $true; break }
        $cancel = Test-Cancelled
        $poll = Invoke-Guest 'Poll' @{ runId = $runId; cancel = $cancel; waitSeconds = 10; cortexRelay = [bool]$script:request.cortexRelay } 90
        if ($script:request.cortexRelay -and $poll.PSObject.Properties['relay']) {
            $reply = Invoke-CortexModel $poll.relay
            $null = Invoke-Guest 'RelayReply' @{ runId=$runId; response=$reply } 90
        }
        if ($cancel -and -not $cancelForwardedAt) { $cancelForwardedAt = [DateTimeOffset]::UtcNow; $result.cancelForwarded = $true; Emit 'cancelling' 'Cancellation forwarded to the worker in the VM.' }
        $taskState = [string]$poll.taskState
        if ($poll.exited) { $exit = $poll.exit; break }
        if ($taskState -notin @('Running', 'Queued')) { $result.taskEndedWithoutExit = $true; break }
        if ($cancelForwardedAt -and ([DateTimeOffset]::UtcNow - $cancelForwardedAt).TotalSeconds -gt ([int]$script:request.shutdownGraceSeconds + 120)) { break }
    }
    $result.exit = $exit; $result.taskState = $taskState
    if (-not $exit) { $result.stage = 'stopping'; Emit 'stopping' 'Stopping the run task in the VM.'; $result.stop = Invoke-Guest 'StopRun' @{ runId = $runId } 120 }

    $result.stage = 'collect'; Emit 'collect' 'Copying screenshots and results back from the VM.'
    $hostZip = Join-Path ([string]$script:request.hostDirectory) 'evidence.zip'
    $pack = Invoke-Guest 'PackEvidence' @{ runId = $runId } 900 $null $null @(@{ source = ($GuestRoot + '\Runs\' + $runId + '.evidence.zip'); destination = $hostZip })
    if ((Hash-File $hostZip) -cne [string]$pack.zipSha256) { throw 'Evidence transfer hash differs.' }
    $result.evidence = [ordered]@{ zip = $hostZip; zipSha256 = [string]$pack.zipSha256; zipBytes = [long]$pack.zipBytes; files = @($pack.files); keyFilePresentAtCollection = [bool]$pack.keyFilePresentAtCollection }
    $result.status = if ($exit) { 'exited' } elseif ($result.Contains('hostDeadlineExceeded')) { 'timedOut' } elseif ($cancelForwardedAt) { 'cancelled' } else { 'failed' }
    $result.completed = $true
}
catch {
    $result.message = Safe-Text $_.Exception.Message
    if ($result.stage -in @('check', 'start') -and $result.status -eq 'failed') { $result.status = 'unavailable' }
}
finally {
    try {
        if ($Operation -eq 'Run' -and $result.Contains('staged') -and $result.staged) {
            Emit 'cleanup' 'Removing the run task and any leftover key from the VM.'
            $keep = -not $result.Contains('evidence')
            $result.cleanup = Invoke-Guest 'Cleanup' @{ runId = [string]$script:request.runId; removeRunDirectory = (-not $keep) } 180
        }
    }
    catch { $result.cleanupError = Safe-Text $_.Exception.Message }
    finally {
        if ($script:credential) { $script:credential.Password.Dispose(); $script:credential = $null }
        if ($providerKey) { $providerKey.Dispose(); $providerKey = $null }
        $result.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
        Save-Result $result
    }
}
