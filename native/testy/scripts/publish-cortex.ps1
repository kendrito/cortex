param([string]$Dotnet = $env:CORTEX_DOTNET, [string]$Output = '', [switch]$Force)
$ErrorActionPreference = 'Stop'
# Node/pnpm can preserve PowerShell 7 module paths when launching Windows PowerShell 5.1.
# Load this process's own utility module so hashing never autoloads an incompatible copy.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '../..'))
if (-not $Dotnet) { $Dotnet = 'dotnet' }
if (-not $Output) { $Output = Join-Path $repoRoot 'packages/extensions/testy/runtime/win-x64' }
$destination = [IO.Path]::GetFullPath($Output)
if (-not $destination.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cortex runtime output must be inside this repository.' }
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|verification|\.git)[\\/]' } | Sort-Object FullName | ForEach-Object {
    @{ path = $_.FullName.Substring($sourceRoot.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
function Fingerprint($Files) { (($Files | Sort-Object { $_.path } | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n") }
if (-not $Force -and (Test-Path -LiteralPath (Join-Path $destination 'cortex-source-manifest.json')) -and (Test-Path -LiteralPath (Join-Path $destination 'release-manifest.json'))) {
    try {
        $cached = Get-Content -LiteralPath (Join-Path $destination 'cortex-source-manifest.json') -Raw | ConvertFrom-Json
        $release = Get-Content -LiteralPath (Join-Path $destination 'release-manifest.json') -Raw | ConvertFrom-Json
        $valid = (Fingerprint $sourceFiles) -ceq (Fingerprint $cached.files)
        foreach ($entry in $release.files) {
            $candidate = [IO.Path]::GetFullPath((Join-Path $destination $entry.path))
            if (-not $candidate.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or (Get-FileHash -LiteralPath $candidate).Hash -cne $entry.sha256) { $valid = $false; break }
        }
        if ($valid) { Write-Output "Cortex Testy runtime is current: $destination"; return }
    } catch { Write-Output 'Rebuilding stale Testy runtime.' }
}
$dotnetCommand = (Get-Command $Dotnet -ErrorAction Stop).Source
$stage = Join-Path (Split-Path -Parent $destination) ('.publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$priorTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$priorFirstRun = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
try {
    foreach ($project in @('Testy.Cli','Testy.Studio','Testy.Agent','Testy.TestLab','Testy.OrderLab','Testy.WpfLab')) {
        & $dotnetCommand publish (Join-Path $sourceRoot "src/$project/$project.csproj") -c Release -r win-x64 --self-contained true -o $stage --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'examples') -Destination (Join-Path $stage 'examples') -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs') -Destination (Join-Path $stage 'docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'src/Testy.Cli/Mcp/AGENT-ACCESS.md') -Destination (Join-Path $stage 'AGENT-ACCESS.md')
    $licenses = Join-Path $stage 'licenses'; New-Item -ItemType Directory -Path $licenses | Out-Null
    $sdkRoot = Split-Path -Parent $dotnetCommand
    foreach ($notice in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        $noticePath = Join-Path $sdkRoot $notice
        if (-not (Test-Path -LiteralPath $noticePath)) { throw "Required .NET notice missing: $noticePath" }
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $licenses ('dotnet-' + $notice))
    }
    $desktopSdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'sdk') -Directory | Sort-Object Name -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Sdks/Microsoft.NET.Sdk.WindowsDesktop/LICENSE.TXT') } | Select-Object -First 1
    if (-not $desktopSdk) { throw 'Required WindowsDesktop .NET notices were not found.' }
    foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) { Copy-Item -LiteralPath (Join-Path $desktopSdk.FullName ('Sdks/Microsoft.NET.Sdk.WindowsDesktop/' + $notice)) -Destination (Join-Path $licenses ('windowsdesktop-' + $notice)) }
    $version = [string]([xml](Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $afterSourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|verification|\.git)[\\/]' } | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($sourceRoot.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    if ((Fingerprint $sourceFiles) -cne (Fingerprint $afterSourceFiles)) { throw 'Testy source changed during publish. Previous runtime is intact; run publish again after edits finish.' }
    @{ schema='testy.build-sources.v1'; version=$version; files=$sourceFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'build-sources.json') -Encoding utf8
    @{ schema='cortex.testy-source.v1'; sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'build-sources.json')).Hash; sdk=(& $dotnetCommand --version); files=$sourceFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'cortex-source-manifest.json') -Encoding utf8
    @{ product='Testy for Cortex'; version=$version; runtimeIdentifier='win-x64'; selfContained=$true; entryPoint='Testy.Studio.exe'; sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'build-sources.json')).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'bundle.json') -Encoding utf8
    $description = & (Join-Path $stage 'Testy.Cli.exe') mcp --describe --format server-json --relative | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Published MCP discovery failed.' }
    $localServer = ($description | ConvertFrom-Json)._meta.'io.modelcontextprotocol.registry/publisher-provided'.localServer
    if ($localServer.commandIsRelativeTo -cne 'the folder containing this file' -or @($localServer.transports).Count -ne 2) {
        throw 'Published MCP metadata must describe both transports relative to the package directory.'
    }
    foreach ($transport in $localServer.transports) {
        if ($transport.command -cne 'Testy.Cli.exe' -or @($transport.args).Count -eq 0 -or $transport.args[0] -cne 'mcp') {
            throw 'Published MCP metadata contains a non-portable executable reference.'
        }
    }
    [IO.File]::WriteAllText((Join-Path $stage 'mcp-server.json'), $description.Trim() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'seal-package.ps1') -PackageDirectory $stage
    # Preserve any previous runtime outside the active path. All moves stay inside the verified output parent.
    if (Test-Path -LiteralPath $destination) {
        $old = Join-Path (Split-Path -Parent $destination) ('.previous-' + [Guid]::NewGuid().ToString('N'))
        if (-not $old.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime backup escaped repository.' }
        Move-Item -LiteralPath $destination -Destination $old
    }
    Move-Item -LiteralPath $stage -Destination $destination
    Write-Output "Cortex Testy runtime: $destination"
} finally { $env:DOTNET_CLI_TELEMETRY_OPTOUT = $priorTelemetry; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $priorFirstRun }
