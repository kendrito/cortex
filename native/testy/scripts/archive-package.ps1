param([string]$PackageDirectory = '', [string]$ArchivePath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $projectRoot 'dist/Testy' }
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$bundlePath = Join-Path $packagePath 'bundle.json'
if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) { throw 'Publish the package before archiving it.' }
$bundle = Get-Content -LiteralPath $bundlePath -Raw | ConvertFrom-Json
$archiveName = if ($bundle.backendOnly) { 'Testy-Backend-win-x64.zip' } else { 'Testy-win-x64.zip' }
if (-not $ArchivePath) { $ArchivePath = Join-Path $projectRoot "dist/$archiveName" }
$archiveFile = [IO.Path]::GetFullPath($ArchivePath)
if ($archiveFile.StartsWith($packagePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The archive must be outside the package directory.'
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archiveFile) | Out-Null
Compress-Archive -LiteralPath $packagePath -DestinationPath $archiveFile -CompressionLevel Optimal -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archiveFile)
try {
    $packagePrefix = (Split-Path -Leaf $packagePath) + '/'
    $expectedFiles = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $packagePath -File -Recurse -Force) {
        $relativeName = $file.FullName.Substring($packagePath.TrimEnd('\').Length + 1).Replace('\', '/')
        $expectedFiles.Add($packagePrefix + $relativeName, $file.FullName)
    }
    $verifiedFiles = 0
    foreach ($entry in $archive.Entries) {
        $entryName = $entry.FullName.Replace('\', '/')
        if ($entryName.EndsWith('/')) { continue }
        if (-not $expectedFiles.ContainsKey($entryName)) { throw "Unexpected or duplicate archive entry: $entryName" }
        $sourcePath = $expectedFiles[$entryName]
        if ($entry.Length -ne (Get-Item -LiteralPath $sourcePath).Length) { throw "Archive length mismatch: $entryName" }
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        if ($entryHash -cne $sourceHash) { throw "Archive content mismatch: $entryName" }
        [void]$expectedFiles.Remove($entryName)
        $verifiedFiles++
    }
    if ($expectedFiles.Count -gt 0) { throw "Archive is missing $($expectedFiles.Count) published file(s)." }
    if ($verifiedFiles -eq 0) { throw 'Archive contains no published files.' }
    $entryCount = $archive.Entries.Count
} finally { $archive.Dispose() }
$hash = Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256
@{
    archive = $archiveFile
    sha256 = $hash.Hash
    bytes = (Get-Item -LiteralPath $archiveFile).Length
    entries = $entryCount
    verifiedFiles = $verifiedFiles
    version = $bundle.version
    integrity = 'Every archived file matches the published file length and SHA256; no missing or extra files.'
    builtAtUtc = $bundle.builtAtUtc
    archivedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath ($archiveFile + '.json') -Encoding UTF8
Write-Output "Packaged archive: $archiveFile"
Write-Output "SHA256: $($hash.Hash)"
