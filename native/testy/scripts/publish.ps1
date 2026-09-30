param([string]$DotNet = 'dotnet', [switch]$FrameworkDependent, [switch]$BackendOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$bundleName = if ($BackendOnly) { 'Testy.Backend' } else { 'Testy' }
$finalDestination = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist\$bundleName"))
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')).TrimEnd('\')
$destination = Join-Path $distRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
function Assert-PublishPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish paths cannot traverse reparse points.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-PublishPath $destination
Assert-PublishPath $finalDestination
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$projects = if ($BackendOnly) { @('Testy.Cli', 'Testy.Agent', 'Testy.TestLab', 'Testy.OrderLab', 'Testy.WpfLab') } else { @('Testy.Studio', 'Testy.Cli', 'Testy.Agent', 'Testy.TestLab', 'Testy.OrderLab', 'Testy.WpfLab') }
foreach ($project in $projects) {
    & $DotNet publish (Join-Path $projectRoot "src\$project\$project.csproj") -c Release -r win-x64 --self-contained $selfContained -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
$readme = Get-Content -LiteralPath (Join-Path $projectRoot 'README.md') -Raw -Encoding UTF8
if ($FrameworkDependent) { $readme = $readme.Replace('The portable x64 release includes the .NET runtime; installation is not required.', 'This framework-dependent x64 release requires the .NET 9 Windows Desktop Runtime (x64).') }
if ($BackendOnly) { $readme = '> This backend bundle omits Studio. Start with START-HERE.txt for CLI commands; the product documentation below also describes the full Studio bundle.' + [Environment]::NewLine + [Environment]::NewLine + $readme }
$readme.Replace('dist/Testy/Testy.Studio.exe', 'Testy.Studio.exe').Replace('](verification/FINAL-RESULTS.md)', '](BASELINE-VERIFICATION.md)').Replace('](verification/FUNCTIONALITY-RESULTS.md)', '](FUNCTIONALITY-VERIFICATION.md)').Replace('](verification/BACKEND-RESULTS.md)', '](BACKEND-VERIFICATION.md)').Replace('](verification/OPENROUTER-RESULTS.md)', '](OPENROUTER-VERIFICATION.md)') | Set-Content -LiteralPath (Join-Path $destination 'README.md') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $projectRoot 'ARCHITECTURE.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $destination -Recurse -Force
foreach ($document in Get-ChildItem -LiteralPath (Join-Path $destination 'docs') -File -Filter '*.md') {
    $documentText = Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
    $documentText = $documentText.Replace('](../verification/FUNCTIONALITY-RESULTS.md)', '](../FUNCTIONALITY-VERIFICATION.md)').Replace('](../verification/FINAL-RESULTS.md)', '](../BASELINE-VERIFICATION.md)').Replace('](../verification/OPENROUTER-RESULTS.md)', '](../OPENROUTER-VERIFICATION.md)').Replace('](../verification/BACKEND-RESULTS.md)', '](../BACKEND-VERIFICATION.md)')
    # Detailed evidence stays in the source workspace. Preserve its location as text
    # instead of shipping a broken relative link to an unbundled report.
    $documentText = [regex]::Replace($documentText, '(?<!!)\[(?<label>[^\]]+)\]\(\.\./verification/(?<target>[^)]+)\)', [System.Text.RegularExpressions.MatchEvaluator]{
        param($linkMatch)
        return $linkMatch.Groups['label'].Value + ' (source workspace: verification/' + $linkMatch.Groups['target'].Value + ')'
    })
    # Written without a byte order mark (Set-Content -Encoding UTF8 adds one under Windows PowerShell 5.1), like the other bundled documents.
    [IO.File]::WriteAllText($document.FullName, $documentText, [Text.UTF8Encoding]::new($false))
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'examples')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'examples') -Destination $destination -Recurse -Force }
$verificationSummary = Join-Path $projectRoot 'verification/FUNCTIONALITY-RESULTS.md'
if (-not (Test-Path -LiteralPath $verificationSummary)) { $verificationSummary = Join-Path $projectRoot 'verification/FINAL-RESULTS.md' }
if (-not (Test-Path -LiteralPath $verificationSummary)) { $verificationSummary = Join-Path $projectRoot 'verification/BACKEND-RESULTS.md' }
function Write-PackagedSummary([string]$SourcePath, [string]$OutputName) {
    $sourceReport = Get-Content -LiteralPath $SourcePath -Raw -Encoding UTF8
    $packagedReport = [regex]::Replace($sourceReport, '(?<!!)\[(?<label>[^\]]+)\]\((?<target>[^)]+)\)', [System.Text.RegularExpressions.MatchEvaluator]{
        param($linkMatch)
        $reportTarget = $linkMatch.Groups['target'].Value
        $reportLabel = $linkMatch.Groups['label'].Value
        if ($reportTarget -match '^https?://' -or $reportTarget.StartsWith('#')) { return $linkMatch.Value }
        if ($reportTarget.StartsWith('../docs/')) { return '[' + $reportLabel + '](' + $reportTarget.Substring(3) + ')' }
        return $reportLabel + ' (source workspace: verification/' + $reportTarget + ')'
    })
    $packageNotice = '> This portable bundle includes the verification summary. Detailed JSON, screenshots, and failed-attempt evidence remain in the source workspace verification directory. Reports recorded before the rename call the product Axiom.'
    ($packageNotice + [Environment]::NewLine + [Environment]::NewLine + $packagedReport) | Set-Content -LiteralPath (Join-Path $destination $OutputName) -Encoding UTF8
}
if (Test-Path -LiteralPath $verificationSummary) { Write-PackagedSummary $verificationSummary 'VERIFICATION.md' }
foreach ($historical in @(@{source='FINAL-RESULTS.md';output='BASELINE-VERIFICATION.md'}, @{source='BACKEND-RESULTS.md';output='BACKEND-VERIFICATION.md'})) {
    $historicalPath = Join-Path $projectRoot ('verification/' + $historical.source)
    if (Test-Path -LiteralPath $historicalPath) { Write-PackagedSummary $historicalPath $historical.output }
}
$openRouterSummary = Join-Path $projectRoot 'verification/OPENROUTER-RESULTS.md'
if (Test-Path -LiteralPath $openRouterSummary) {
    ('> Recorded before the product was renamed from Axiom to Testy.' + [Environment]::NewLine + [Environment]::NewLine + (Get-Content -LiteralPath $openRouterSummary -Raw -Encoding UTF8)) | Set-Content -LiteralPath (Join-Path $destination 'OPENROUTER-VERIFICATION.md') -Encoding UTF8
}
$functionalitySummary = Join-Path $projectRoot 'verification/FUNCTIONALITY-RESULTS.md'
if (Test-Path -LiteralPath $functionalitySummary) {
    Write-PackagedSummary $functionalitySummary 'FUNCTIONALITY-VERIFICATION.md'
}
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in '.cs','.xaml','.csproj','.manifest' } | Sort-Object FullName | ForEach-Object {
    @{ path=$_.FullName.Substring($projectRoot.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$sourceFiles += @{path='Directory.Build.props'; sha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Algorithm SHA256).Hash}
@{ schema='testy.build-sources.v1'; version=[string]$releaseVersion; files=$sourceFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'build-sources.json') -Encoding UTF8
@{
    product = 'Testy'
    version = [string]$releaseVersion
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runtimeIdentifier = 'win-x64'
    selfContained = -not $FrameworkDependent.IsPresent
    backendOnly = $BackendOnly.IsPresent
    projects = $projects
    sourceManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'build-sources.json') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'bundle.json') -Encoding UTF8
if ($BackendOnly) {
    @'
TESTY BACKEND PREVIEW

This bundle contains Testy.Cli.exe and three owned test fixtures:
Testy.TestLab.exe, Testy.OrderLab.exe, and Testy.WpfLab.exe.
For the complete GUI use the full Testy Windows bundle.
Keep all files in this directory together.

From PowerShell in this folder:
  .\Testy.Cli.exe --help
  .\Testy.TestLab.exe
  .\Testy.Cli.exe list-targets

Use the Lab process ID from list-targets:
  .\Testy.Cli.exe run --pid PID --test .\examples\create-customer.json --artifacts .\artifacts
  .\Testy.Cli.exe run-ai --pid PID --test .\examples\create-customer.json --settings .\examples\provider-codex.json --artifacts .\artifacts

run-ai uses an installed, authenticated Codex CLI by default. Review
examples/provider-codex.json for the executable path and model settings.
Other providers are described in docs/COMPUTER-USE.md. No credentials are
bundled. Desktop input requires an unlocked interactive Windows session.

VERIFICATION.md describes successful checks and provider/desktop limitations.
Detailed evidence paths in that file refer to the source
workspace's verification directory. This is an unsigned development preview.
'@ | Set-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Encoding UTF8
}
else {
    @'
TESTY WINDOWS TEST STUDIO

Launch Testy.Studio.exe. Keep this folder's files together.
This is an unsigned development preview.

1. Click Sample app to open the included Customer Desk app and connect to it.
2. Select Create a customer and click Run (F5).
3. The default Codex provider uses your installed, signed-in Codex CLI.
   It chooses each action, receives fresh evidence, and chooses again.
4. Review Last run, Screenshots, and Results for the outcome.

Settings configures the AI service/model and how tests run.
Let the AI guide each run is on by default. Turn it off for exact
replay; Offline mode needs replay. Click Save to apply.
OpenAI native computer use requires a supported model, your API key variable,
and suitable UI Automation input support. Compatible models use local tools.

Use the Assistant to create or improve editable tests from natural language.
Connect your own app with Change app (Open an app, or pick a window). The WPF
helper is opt-in; see docs/PROBE.md. Stop cancels the current operation.

Run desktop tests in the same unlocked interactive Windows session as the
target app. Do not use a disconnected session or move the pointer during
native input. Invalid screenshots fail explicitly.

See README.md for usage/build instructions and VERIFICATION.md for the checks
actually performed. Detailed evidence paths refer to the source workspace.
'@ | Set-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Encoding UTF8
}
$runtimeNotice = if ($FrameworkDependent) { 'This framework-dependent bundle requires the Microsoft .NET 9 Windows Desktop Runtime (x64).' } else { 'This self-contained bundle includes its .NET runtime; no separate .NET installation is required.' }
Add-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Value ([Environment]::NewLine + $runtimeNotice) -Encoding UTF8
Add-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Value ([Environment]::NewLine + 'Version 0.6 adds Automate in Studio: project commands, preparation before launch, test sets and data, recordings, durable local schedules, and verified backup/restore. API credentials can use Windows Credential Manager. See docs/PRODUCT-WORKFLOWS.md and docs/INSTALLATION.md. Studio uses the native Windows 11 look and follows the system theme.') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-testy.ps1') -Destination $destination
# MCP discovery for AI harnesses: a short access note and an MCP Registry-shaped server.json, generated by the published CLI itself
# with commands relative to the bundle root so the folder stays portable. The CLI writes UTF-8; read it as such.
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Testy.Cli\Mcp\AGENT-ACCESS.md') -Destination (Join-Path $destination 'AGENT-ACCESS.md') -Force
$previousOutputEncoding = [Console]::OutputEncoding
$previousDotNetRoot = $env:DOTNET_ROOT
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    # A framework-dependent Testy.Cli.exe finds its runtime through DOTNET_ROOT; point it at the SDK that just published it.
    $env:DOTNET_ROOT = Split-Path -Parent (Get-Command $DotNet -ErrorAction Stop).Source
    $serverJson = & (Join-Path $destination 'Testy.Cli.exe') mcp --describe --format server-json --relative | Out-String
}
finally { [Console]::OutputEncoding = $previousOutputEncoding; $env:DOTNET_ROOT = $previousDotNetRoot }
if ($LASTEXITCODE -ne 0 -or -not $serverJson.Trim().StartsWith('{')) { throw 'Testy.Cli.exe mcp --describe --format server-json did not produce the MCP server description.' }
[IO.File]::WriteAllText((Join-Path $destination 'mcp-server.json'), $serverJson.Trim() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'seal-package.ps1') -PackageDirectory $destination
if (-not $destination.StartsWith($distRoot+'\', [StringComparison]::OrdinalIgnoreCase) -or -not $finalDestination.StartsWith($distRoot+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish paths escaped dist.' }
foreach ($candidate in @($distRoot, $destination, $finalDestination)) {
    if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish destination cannot be a reparse point.' }
}
$previous = $null
if (Test-Path -LiteralPath $finalDestination) {
    $previous = Join-Path $distRoot ('history\' + $bundleName + '-previous-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    Assert-PublishPath $previous
    New-Item -ItemType Directory -Path (Split-Path -Parent $previous) -Force | Out-Null
    if (-not ([IO.Path]::GetFullPath($previous)).StartsWith($distRoot+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'History path escaped dist.' }
    Move-Item -LiteralPath $finalDestination -Destination $previous
}
try { Move-Item -LiteralPath $destination -Destination $finalDestination }
catch { if ($previous -and -not (Test-Path -LiteralPath $finalDestination)) { Move-Item -LiteralPath $previous -Destination $finalDestination }; throw }
$destination = $finalDestination
if ($BackendOnly) { Write-Output "Portable Windows backend: $destination\Testy.Cli.exe" }
else { Write-Output "Portable Windows application: $destination\Testy.Studio.exe" }
