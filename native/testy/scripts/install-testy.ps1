param(
    [ValidateSet('Install','Update','Rollback','Uninstall','Verify')][string]$Action = 'Install',
    [string]$PackageDirectory = '',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Testy'),
    [string]$PublisherThumbprint = '',
    [switch]$AllowUnsigned,
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$markerPath = Join-Path $installRoot 'testy-install.json'
$versions = Join-Path $installRoot 'versions'
$transactionPath = Join-Path $installRoot 'install-transaction.json'
function Assert-NoReparse([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) { if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not supported: $current" } }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }; $current = $parent
    }
}
function Assert-OwnedChild([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installation path escaped its owned root.' }
    Assert-NoReparse $full
    return $full
}
function Assert-Stopped {
    foreach ($process in Get-Process -Name 'Testy*' -ErrorAction SilentlyContinue) {
        try { $path = $process.Path } catch { throw 'Cannot verify whether an Testy process is using this installation.' }
        if ($path -and $path.StartsWith($installRoot+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Close Testy and its installed test fixtures before changing this installation.' }
    }
}
function Read-State {
    Assert-NoReparse $markerPath
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'This directory is not an Testy-managed installation.' }
    $state = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($state.schema -ne 'testy.install.v1' -or $state.root -ne $installRoot) { throw 'Invalid installation ownership marker.' }
    if ($state.current -notmatch '^\d+\.\d+\.\d+$' -or ($state.previous -and $state.previous -notmatch '^\d+\.\d+\.\d+$')) { throw 'Invalid installed version identity.' }
    if ($state.shortcut -and $state.shortcut -ne (Join-Path ([Environment]::GetFolderPath('Programs')) 'Testy.lnk')) { throw 'Invalid installed shortcut identity.' }
    return $state
}
function Verify-Package([string]$Directory) {
    $root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\'); Assert-NoReparse $root
    foreach ($entry in Get-ChildItem -LiteralPath $root -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a reparse point.' } }
    $manifestFile = Join-Path $root 'release-manifest.json'; $signatureFile = Join-Path $root 'release-manifest.p7s'
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw 'A sealed release manifest is required.' }
    if (Test-Path -LiteralPath $signatureFile) {
        if (-not $PublisherThumbprint) { throw 'Supply the independently verified publisher certificate thumbprint.' }
        Add-Type -AssemblyName System.Security
        try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs } catch { }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifestFile)), $true)
        $cms.Decode([IO.File]::ReadAllBytes($signatureFile)); $cms.CheckSignature($false)
        if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $PublisherThumbprint.Replace(' ','')) { throw 'Release publisher does not match the pinned certificate.' }
    } elseif (-not $AllowUnsigned) { throw 'Unsigned package rejected. Development builds require explicit -AllowUnsigned.' }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 'testy.release.v1' -or $manifest.product -ne 'Testy' -or $manifest.runtime -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unsupported release manifest.' }
    if ([bool]$manifest.signed -ne (Test-Path -LiteralPath $signatureFile)) { throw 'Manifest signature declaration is inconsistent.' }
    if ($manifest.files.Count -lt 1 -or $manifest.files.Count -gt 20000) { throw 'Invalid release file count.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ($file.path -notmatch '^[^:]+$' -or $file.path -match '(^/|\\|(^|/)\.\.?(/|$)|[. ](/|$))' -or $file.path -in 'release-manifest.json','release-manifest.p7s' -or -not $expected.Add($file.path)) { throw 'Invalid or duplicate release path.' }
        $path = Assert-OwnedChild (Join-Path $root $file.path) $root
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Release integrity failed: $($file.path)" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relative = $file.FullName.Substring($root.Length+1).Replace('\','/')
        if ($relative -notin 'release-manifest.json','release-manifest.p7s' -and -not $expected.Contains($relative)) { throw "Unlisted package file: $relative" }
    }
    foreach ($required in 'Testy.Studio.exe','Testy.Cli.exe','Testy.Agent.exe','bundle.json') { if (-not $expected.Contains($required)) { throw "Package is missing $required" } }
    return $manifest
}
function Write-State($State) {
    $temp = Join-Path $installRoot ('state-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try { $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8; Move-Item -LiteralPath $temp -Destination $markerPath -Force }
    finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Write-Transaction($State) {
    $temp = Join-Path $installRoot ('transaction-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json -Depth 12))
        $file = [IO.File]::Open($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
        Move-Item -LiteralPath $temp -Destination $transactionPath -Force
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Shortcut-Snapshot {
    if ($NoShortcut) { return $null }
    $path = Join-Path ([Environment]::GetFolderPath('Programs')) 'Testy.lnk'; Assert-NoReparse $path
    $exists = Test-Path -LiteralPath $path -PathType Leaf
    if ($exists) {
        $shell = New-Object -ComObject WScript.Shell; $shortcut = $shell.CreateShortcut($path)
        if (-not $shortcut.TargetPath -or -not $shortcut.TargetPath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'An unrelated Testy shortcut already exists.' }
        if ((Get-Item -LiteralPath $path).Length -gt 1048576) { throw 'Unexpected shortcut size.' }
    }
    return @{path=$path; existed=[bool]$exists; bytes=if($exists){[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))}else{''}}
}
function Restore-Shortcut($Snapshot) {
    if ($null -eq $Snapshot) { return }
    $expected = Join-Path ([Environment]::GetFolderPath('Programs')) 'Testy.lnk'
    if ($Snapshot.path -ne $expected) { throw 'Transaction shortcut path is invalid.' }
    Assert-NoReparse $expected
    if (Test-Path -LiteralPath $expected) {
        $shell = New-Object -ComObject WScript.Shell; $shortcut = $shell.CreateShortcut($expected)
        if (-not $shortcut.TargetPath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Shortcut changed outside this installation; recovery requires review.' }
    }
    if ($Snapshot.existed) { [IO.File]::WriteAllBytes($expected,[Convert]::FromBase64String($Snapshot.bytes)) }
    elseif (Test-Path -LiteralPath $expected) { Remove-Item -LiteralPath $expected -Force }
}
function Begin-Activation([string]$Version, [string]$Stage, [bool]$OwnNewTarget) {
    $transaction = @{schema='testy.install-transaction.v1';root=$installRoot;version=$Version;stage=$Stage;ownNewTarget=$OwnNewTarget;
        previousState=if(Test-Path -LiteralPath $markerPath -PathType Leaf){[Convert]::ToBase64String([IO.File]::ReadAllBytes($markerPath))}else{''};shortcut=(Shortcut-Snapshot)}
    Write-Transaction $transaction
}
function Recover-Activation {
    Assert-NoReparse $transactionPath
    if (-not (Test-Path -LiteralPath $transactionPath -PathType Leaf)) { return }
    $transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
    if ($transaction.schema -ne 'testy.install-transaction.v1' -or $transaction.root -ne $installRoot -or $transaction.version -notmatch '^\d+\.\d+\.\d+$' -or
        ($transaction.stage -and $transaction.stage -notmatch '^\.stage-[a-f0-9]{32}$')) { throw 'Invalid installation transaction; no files were removed.' }
    $currentState = if(Test-Path -LiteralPath $markerPath -PathType Leaf){Read-State}else{$null}
    if ($currentState -and $currentState.current -eq $transaction.version) {
        $null = Verify-Package (Assert-OwnedChild (Join-Path $versions $transaction.version) $installRoot)
        Remove-Item -LiteralPath $transactionPath -Force; return # State was committed after shortcut activation.
    }
    $currentBytes = if(Test-Path -LiteralPath $markerPath -PathType Leaf){[Convert]::ToBase64String([IO.File]::ReadAllBytes($markerPath))}else{''}
    if ($currentBytes -cne $transaction.previousState) { throw 'Installation state changed outside the pending transaction; recovery requires review.' }
    Restore-Shortcut $transaction.shortcut
    if ($transaction.ownNewTarget) {
        $target = Assert-OwnedChild (Join-Path $versions $transaction.version) $installRoot
        if (Test-Path -LiteralPath $target) {
            foreach ($entry in Get-ChildItem -LiteralPath $target -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery target contains an unexpected reparse point.' } }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
    if ($transaction.stage) {
        $stage = Assert-OwnedChild (Join-Path $versions $transaction.stage) $installRoot
        if (Test-Path -LiteralPath $stage) {
            foreach ($entry in Get-ChildItem -LiteralPath $stage -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery staging contains an unexpected reparse point.' } }
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
    Remove-Item -LiteralPath $transactionPath -Force
}
function Set-Shortcut([string]$VersionPath) {
    if ($NoShortcut) { return $null }
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Testy.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcut.TargetPath -and -not $shortcut.TargetPath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'An unrelated Testy shortcut already exists.' }
    $shortcut.TargetPath = Join-Path $VersionPath 'Testy.Studio.exe'; $shortcut.WorkingDirectory = $VersionPath; $shortcut.Description = 'Testy Windows test studio'; $shortcut.Save()
    return $shortcutPath
}
Assert-NoReparse $installRoot
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot) -or $installRoot -eq [Environment]::GetFolderPath('UserProfile')) { throw 'Choose a dedicated installation directory.' }
if ($Action -eq 'Verify') { if (-not $PackageDirectory) { throw 'Provide -PackageDirectory.' }; $manifest = Verify-Package $PackageDirectory; @{ verified=$true; version=$manifest.version; files=$manifest.files.Count } | ConvertTo-Json; return }
if (Test-Path -LiteralPath $installRoot) {
    if (-not (Test-Path -LiteralPath $markerPath) -and -not (Test-Path -LiteralPath $transactionPath) -and @(Get-ChildItem -LiteralPath $installRoot -Force).Count -gt 0) { throw 'The destination is nonempty and is not an Testy-managed installation.' }
} else { New-Item -ItemType Directory -Path $installRoot | Out-Null }
$lockPath = Join-Path $installRoot 'install.lock'
Assert-NoReparse $lockPath
$lease = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$staging = $null
try {
    Assert-Stopped
    Recover-Activation
    $state = if (Test-Path -LiteralPath $markerPath) { Read-State } else { $null }
    if ($NoShortcut -and $state -and $state.shortcut -and $Action -in 'Install','Update','Rollback') { throw 'This installation owns a shortcut. Omit -NoShortcut so activation updates it consistently.' }
    if ($Action -in 'Update','Rollback','Uninstall' -and -not $state) { throw 'The selected action requires an existing managed installation.' }
    if ($Action -eq 'Uninstall') {
        foreach ($entry in Get-ChildItem -LiteralPath $versions -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation contains an unexpected reparse point.' } }
        $ownedVersions = Assert-OwnedChild $versions $installRoot
        if ($state.shortcut -and (Test-Path -LiteralPath $state.shortcut)) {
            $shell = New-Object -ComObject WScript.Shell; $shortcut = $shell.CreateShortcut($state.shortcut)
            if ($shortcut.TargetPath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $state.shortcut -Force }
        }
        Remove-Item -LiteralPath $ownedVersions -Recurse -Force
        Remove-Item -LiteralPath $markerPath -Force
        @{ completed=$true; action=$Action; message='Installed versions and owned shortcut removed. Workspaces and credentials are retained. If the background agent was enabled, remove it with Testy.Agent.exe uninstall (administrator approval); it runs from Program Files and is not removed by this script.' } | ConvertTo-Json
        return
    }
    if ($Action -eq 'Rollback') {
        if (-not $state.previous) { throw 'No prior version is recorded.' }
        $previous = Assert-OwnedChild (Join-Path $versions $state.previous) $installRoot; $null = Verify-Package $previous
        Begin-Activation $state.previous '' $false
        $old = $state.current; $state.current = $state.previous; $state.previous = $old; $state.shortcut = Set-Shortcut $previous; Write-State $state
        Remove-Item -LiteralPath $transactionPath -Force
        @{ completed=$true; action=$Action; version=$state.current } | ConvertTo-Json; return
    }
    if (-not $PackageDirectory) { throw 'Provide -PackageDirectory for installation/update.' }
    $source = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\'); $manifest = Verify-Package $source
    if ($state -and [Version]$manifest.version -le [Version]$state.current) { throw 'Updates require a newer version. Use Rollback to return to a recorded previous version.' }
    New-Item -ItemType Directory -Path $versions -Force | Out-Null
    $target = Assert-OwnedChild (Join-Path $versions $manifest.version) $installRoot
    if (Test-Path -LiteralPath $target) { throw 'This version directory already exists; it will not be overwritten.' }
    $staging = Assert-OwnedChild (Join-Path $versions ('.stage-'+[Guid]::NewGuid().ToString('N'))) $installRoot
    Begin-Activation $manifest.version (Split-Path -Leaf $staging) $true
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($file in $manifest.files) {
        $destination = Assert-OwnedChild (Join-Path $staging $file.path) $staging
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $source 'release-manifest.json') -Destination $staging
    if (Test-Path -LiteralPath (Join-Path $source 'release-manifest.p7s')) { Copy-Item -LiteralPath (Join-Path $source 'release-manifest.p7s') -Destination $staging }
    $null = Verify-Package $staging
    Move-Item -LiteralPath $staging -Destination $target; $staging = $null
    $shortcutPath = Set-Shortcut $target
    $next = @{ schema='testy.install.v1'; root=$installRoot; current=$manifest.version; previous=if($state){$state.current}else{$null}; shortcut=$shortcutPath; updatedAt=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-State $next
    Remove-Item -LiteralPath $transactionPath -Force
    @{ completed=$true; action=$Action; version=$manifest.version; executable=(Join-Path $target 'Testy.Studio.exe'); signed=[bool]$manifest.signed; agent='Optional: run Testy.Agent.exe install from this version folder (administrator approval) to run queued and scheduled tests in the background at sign-in. Re-run it after updates.' } | ConvertTo-Json
} catch {
    $originalFailure = $_
    try { Recover-Activation } catch { Write-Warning 'Installation recovery could not complete. The pending transaction is retained; no unrelated files are removed.' }
    throw $originalFailure
} finally {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        $checked = Assert-OwnedChild $staging $installRoot
        foreach ($entry in Get-ChildItem -LiteralPath $checked -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging cleanup rejected an unexpected reparse point.' } }
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
    $lease.Dispose()
    if (Test-Path -LiteralPath $lockPath) { Remove-Item -LiteralPath $lockPath -Force }
    if (-not (Test-Path -LiteralPath $markerPath) -and -not (Test-Path -LiteralPath $transactionPath) -and (Test-Path -LiteralPath $versions) -and @(Get-ChildItem -LiteralPath $versions -Force).Count -eq 0) { Remove-Item -LiteralPath (Assert-OwnedChild $versions $installRoot) -Force }
}
