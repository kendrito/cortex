param([string]$DotNet = 'dotnet', [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$BackendOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projects = @('src/Testy.Cli/Testy.Cli.csproj', 'src/Testy.Agent/Testy.Agent.csproj', 'src/Testy.TestLab/Testy.TestLab.csproj', 'src/Testy.OrderLab/Testy.OrderLab.csproj', 'src/Testy.WpfLab/Testy.WpfLab.csproj', 'tests/Testy.Tests/Testy.Tests.csproj', 'tests/Testy.WindowsChecks/Testy.WindowsChecks.csproj')
if (-not $BackendOnly) { $projects = @('src/Testy.Studio/Testy.Studio.csproj') + $projects }
foreach ($project in $projects) {
    & $DotNet build (Join-Path $projectRoot $project) -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
