Set-StrictMode -Version Latest

function Get-WindowsSignTool {
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $version = Get-ChildItem -LiteralPath $sdkBin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'x64/signtool.exe')) } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $version) { throw 'The x64 Windows SDK SignTool is required.' }
    Join-Path $version.FullName 'x64/signtool.exe'
}

function Assert-EmbeddedSignature {
    param([Parameter(Mandatory)][string]$Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
        throw "Expected a valid embedded Authenticode signature on $Path. Status: $($signature.Status)/$($signature.SignatureType)."
    }
    & (Get-WindowsSignTool) verify /pa /all /v $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification exited $LASTEXITCODE for $Path." }
    $signature
}

function Get-PECertificateTable {
    param([byte[]]$Image)
    # The checksum and certificate-table directory are mutable Authenticode fields in PE32+.
    if ($Image.Length -lt 0x40 -or [BitConverter]::ToUInt16($Image, 0) -ne 0x5a4d) {
        throw 'Expected an MZ header in the Windows publish.'
    }
    $pe = [BitConverter]::ToInt32($Image, 0x3c)
    if ($pe -lt 0x40 -or $pe -gt ($Image.Length - 24 - 152) -or
        [BitConverter]::ToUInt32($Image, $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($Image, $pe + 4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($Image, $pe + 20) -lt 152 -or
        [BitConverter]::ToUInt16($Image, $pe + 24) -ne 0x20b -or
        [BitConverter]::ToUInt32($Image, $pe + 24 + 108) -lt 5) {
        throw 'Expected an x64 PE32+ image with a security directory.'
    }
    $optional = $pe + 24
    $directory = $optional + 112 + (4 * 8)
    [pscustomobject]@{
        Checksum = $optional + 64
        Directory = $directory
        Offset = [long][BitConverter]::ToUInt32($Image, $directory)
        Size = [long][BitConverter]::ToUInt32($Image, $directory + 4)
    }
}

function Get-NormalizedPEHash {
    param([byte[]]$Image, $Table, [int]$Length)
    [Array]::Clear($Image, $Table.Checksum, 4)
    [Array]::Clear($Image, $Table.Directory, 8)
    $stream = [IO.MemoryStream]::new($Image, 0, $Length, $false)
    try { (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
    finally { $stream.Dispose() }
}

function Get-AuthenticodeTimestamp {
    param([byte[]]$Image, $Table, [string]$CertificateSha256)
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $signature = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    $certLength = [long][BitConverter]::ToUInt32($Image, $Table.Offset)
    if ($certLength -le 8 -or $certLength -gt $Table.Size -or $Table.Size - $certLength -gt 7 -or
        [BitConverter]::ToUInt16($Image, $Table.Offset + 4) -ne 0x200 -or
        [BitConverter]::ToUInt16($Image, $Table.Offset + 6) -ne 2) {
        throw 'Expected a single PKCS#7 Authenticode certificate record.'
    }
    $pkcs = [byte[]]::new($certLength - 8)
    [Array]::Copy($Image, $Table.Offset + 8, $pkcs, 0, $pkcs.Length)
    $signature.Decode($pkcs)
    $sha256Oid = '2.16.840.1.101.3.4.2.1'
    if ($signature.ContentInfo.ContentType.Value -ne '1.3.6.1.4.1.311.2.1.4' -or
        $signature.SignerInfos.Count -ne 1 -or $signature.SignerInfos[0].DigestAlgorithm.Value -ne $sha256Oid) {
        throw 'Expected an Authenticode CMS signature with SHA-256.'
    }
    $signer = $signature.SignerInfos[0]
    if ($signer.Certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -ne $CertificateSha256) {
        throw 'The Authenticode CMS signer does not match the approved certificate.'
    }
    # SpcIndirectDataContent contains the hash algorithm for the actual PE image.
    $reader = [System.Formats.Asn1.AsnReader]::new(
        [ReadOnlyMemory[byte]]::new($signature.ContentInfo.Content), [System.Formats.Asn1.AsnEncodingRules]::BER)
    $data = $reader.ReadSequence()
    $null = $data.ReadEncodedValue()
    $digest = $data.ReadSequence()
    $algorithm = $digest.ReadSequence()
    if ($algorithm.ReadObjectIdentifier() -ne $sha256Oid -or $digest.ReadOctetString().Length -ne 32) {
        throw 'The embedded PE image digest is not SHA-256.'
    }

    $attributes = @($signer.UnsignedAttributes | Where-Object { $_.Oid.Value -eq '1.3.6.1.4.1.311.3.3.1' })
    if ($attributes.Count -ne 1 -or $attributes[0].Values.Count -ne 1) { throw 'Missing or ambiguous RFC 3161 timestamp.' }
    $encoded = $attributes[0].Values[0].RawData
    $token = $null
    $consumed = 0
    $tsa = $null
    if (-not [System.Security.Cryptography.Pkcs.Rfc3161TimestampToken]::TryDecode(
            [ReadOnlyMemory[byte]]::new($encoded), [ref]$token, [ref]$consumed) -or $consumed -ne $encoded.Length -or
        $token.TokenInfo.HashAlgorithmId.Value -ne $sha256Oid -or
        -not $token.VerifySignatureForSignerInfo($signer, [ref]$tsa, $signature.Certificates)) {
        throw 'The timestamp is not a valid RFC 3161 token with a SHA-256 imprint.'
    }
    $token.TokenInfo.Timestamp.ToUniversalTime().ToString('o')
}

function Assert-WindowsReleaseSignature {
    param(
        [Parameter(Mandatory)][string]$UnsignedPath,
        [Parameter(Mandatory)][string]$SignedPath,
        [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CertificateSha256
    )
    $signature = Assert-EmbeddedSignature -Path $SignedPath
    $certificate = $signature.SignerCertificate
    if ($certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -ne $CertificateSha256) {
        throw 'The Authenticode publisher certificate does not match the approved SHA-256 fingerprint.'
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
    try {
        if (-not $rsa -or $rsa.KeySize -lt 2048 -or $rsa.KeySize -gt 4096 -or -not $signature.TimeStamperCertificate) {
            throw 'Expected a timestamped Authenticode RSA code signing certificate (2048–4096 bits).'
        }
    } finally { if ($rsa) { $rsa.Dispose() } }

    $unsigned = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $UnsignedPath).Path)
    $signed = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SignedPath).Path)
    $inputTable = Get-PECertificateTable -Image $unsigned
    $outputTable = Get-PECertificateTable -Image $signed
    if ($inputTable.Offset -ne 0 -or $inputTable.Size -ne 0) { throw 'The original build must be unsigned.' }
    if ($outputTable.Offset -lt $unsigned.Length -or $outputTable.Offset - $unsigned.Length -gt 7 -or
        $outputTable.Offset % 8 -ne 0 -or $outputTable.Size -le 8 -or
        $outputTable.Offset + $outputTable.Size -ne $signed.Length) {
        throw 'The signer must append a certificate table after the original PE image.'
    }
    for ($i = $unsigned.Length; $i -lt $outputTable.Offset; $i++) {
        if ($signed[$i] -ne 0) { throw 'Unexpected data between the PE image and the certificate table.' }
    }
    if ((Get-NormalizedPEHash $unsigned $inputTable $unsigned.Length) -ne
        (Get-NormalizedPEHash $signed $outputTable $unsigned.Length)) {
        throw 'The signed executable differs from the verified bundle beyond Authenticode metadata.'
    }

    $timestamp = Get-AuthenticodeTimestamp -Image $signed -Table $outputTable -CertificateSha256 $CertificateSha256
    [pscustomobject]@{
        Publisher = $certificate.Subject
        CertificateSha256 = $CertificateSha256.ToUpperInvariant()
        TimestampUtc = $timestamp
        TimestampAuthority = $signature.TimeStamperCertificate.Subject
        ExecutableSha256 = (Get-FileHash -LiteralPath $SignedPath -Algorithm SHA256).Hash
    }
}

Export-ModuleMember -Function Assert-EmbeddedSignature, Assert-WindowsReleaseSignature
