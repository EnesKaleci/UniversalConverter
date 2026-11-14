param(
    [Parameter(Mandatory)][string]$BundleCache,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsAuthenticode.psm1')

$expected = @('av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')
$bundled = @(Get-ChildItem -LiteralPath $BundleCache -Recurse -File -Force | Sort-Object Name)
$systemSqlite = Get-Item -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'winsqlite3.dll')
$rows = foreach ($file in ($bundled + $systemSqlite)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    [pscustomobject]@{
        Name = $file.Name
        Origin = if ($file.FullName -eq $systemSqlite.FullName) { 'Windows System32' } else { 'Bundle extraction' }
        SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        SignatureStatus = $signature.Status.ToString()
        SignatureType = $signature.SignatureType.ToString()
        Publisher = $signature.SignerCertificate.Subject
        CertificateSha256 = $signature.SignerCertificate?.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
        TimestampAuthority = $signature.TimeStamperCertificate.Subject
        FileVersion = $file.VersionInfo.FileVersion
    }
}
$rows | Export-Csv -LiteralPath $OutputPath -Encoding utf8 -NoTypeInformation
$rows | Format-Table Name, Origin, SignatureStatus, SignatureType, Publisher | Out-Host
if ($bundled.Count -ne $expected.Count -or (Compare-Object $expected @($bundled.Name))) {
    throw 'The extracted payload does not match the reviewed list of native DLLs.'
}
foreach ($dll in $bundled) {
    $null = Assert-EmbeddedSignature -Path $dll.FullName
}
$sqlite = $rows | Where-Object Origin -eq 'Windows System32'
if ($sqlite.SignatureStatus -ne 'Valid' -or $sqlite.Publisher -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
    throw 'Windows System32 SQLite does not have a valid Microsoft Authenticode or catalog signature.'
}
