param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$CertificateThumbprint = '',
    [string]$SignTool = 'signtool.exe',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\')
function Assert-SealPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release package roots and ancestors cannot be reparse points.' }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
}
Assert-SealPath $PackageDirectory
Assert-SealPath $package
if (-not (Test-Path -LiteralPath (Join-Path $package 'bundle.json'))) { throw 'A published bundle.json is required.' }
$bundle = Get-Content -LiteralPath (Join-Path $package 'bundle.json') -Raw | ConvertFrom-Json
if ($bundle.version -notmatch '^\d+\.\d+\.\d+$' -or $bundle.runtimeIdentifier -ne 'win-x64') { throw 'A Windows x64 bundle with a numeric three-part release version is required.' }
foreach ($entry in Get-ChildItem -LiteralPath $package -Force -Recurse) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release packages cannot contain reparse points.' }
}
$certificate = $null
if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[A-F0-9]{40}$') { throw 'A SHA1 certificate thumbprint is required.' }
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $thumbprint)
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt [DateTime]::Now -or $certificate.NotBefore -gt [DateTime]::Now) { throw 'A current certificate with its private key is required.' }
    if (-not ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) { throw 'A code-signing certificate is required.' }
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Filter 'Testy.*' | Where-Object { $_.Extension -in '.exe','.dll' }) {
        & $SignTool sign /sha1 $thumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $($file.Name)" }
        & $SignTool verify /pa $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $($file.Name)" }
    }
}
$manifestPath = Join-Path $package 'release-manifest.json'
$signaturePath = Join-Path $package 'release-manifest.p7s'
if (Test-Path -LiteralPath $signaturePath) { Remove-Item -LiteralPath $signaturePath -Force }
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse -Force | Where-Object { $_.FullName -ne $manifestPath -and $_.FullName -ne $signaturePath } | Sort-Object FullName | ForEach-Object {
    @{ path=$_.FullName.Substring($package.Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ schema='testy.release.v1'; product='Testy'; version=$bundle.version; runtime='win-x64'; signed=[bool]$certificate; files=$files } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
if ($certificate) {
    Add-Type -AssemblyName System.Security
    try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs } catch { }
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifestPath))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::ExcludeRoot
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes($signaturePath, $cms.Encode())
}
@{ sealed=$true; signed=[bool]$certificate; manifest=$manifestPath; files=$files.Count; version=$bundle.version } | ConvertTo-Json
