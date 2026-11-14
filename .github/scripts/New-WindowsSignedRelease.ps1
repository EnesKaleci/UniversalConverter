param(
    [Parameter(Mandatory)][string]$UnsignedZip,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$UnsignedZipSha256,
    [Parameter(Mandatory)][string]$SignedDirectory,
    [Parameter(Mandatory)][string]$NativeInventory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CertificateSha256,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsAuthenticode.psm1')
$exeName = 'UniversalConverter.Desktop.exe'
$zipName = "UniversalConverter-$Version-Windows-x64-SingleFile.zip"

if ((Get-FileHash -LiteralPath $UnsignedZip -Algorithm SHA256).Hash -ne $UnsignedZipSha256) {
    throw 'The unsigned test ZIP no longer matches the build job output.'
}
$returned = @(Get-ChildItem -LiteralPath $SignedDirectory -Force)
if ($returned.Count -ne 1 -or $returned[0].Name -cne $exeName -or $returned[0].PSIsContainer -or
    $returned[0].Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'The signing service must return only the signed Single-file EXE.'
}

$staging = New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP "verified-windows-$([Guid]::NewGuid())")
$unsignedRoot = Join-Path $staging.FullName 'unsigned'
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $UnsignedZip).Path, $unsignedRoot)
$exe = Join-Path $unsignedRoot $exeName
$verified = Assert-WindowsReleaseSignature -UnsignedPath $exe -SignedPath $returned[0].FullName -CertificateSha256 $CertificateSha256
$originalExe = Join-Path $staging.FullName 'original.exe'
Move-Item -LiteralPath $exe -Destination $originalExe
Copy-Item -LiteralPath $returned[0].FullName -Destination $exe
Copy-Item -LiteralPath $NativeInventory -Destination (Join-Path $unsignedRoot 'native-authenticode.csv')
# Replace the unsigned smoke-test report with the release verification report.
Remove-Item -LiteralPath (Join-Path $unsignedRoot 'authenticode.csv')
$verified | Add-Member -NotePropertyMembers @{ SourceCommit = $SourceCommit; Version = $Version }
$verified | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $unsignedRoot 'authenticode.json') -Encoding utf8

$output = New-Item -ItemType Directory -Path $OutputDirectory
$zip = Join-Path $output.FullName $zipName
[IO.Compression.ZipFile]::CreateFromDirectory($unsignedRoot, $zip)
$checkedRoot = Join-Path $staging.FullName 'checked'
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $checkedRoot)
$files = @(Get-ChildItem -LiteralPath $unsignedRoot -Recurse -File -Force)
if (@(Get-ChildItem -LiteralPath $checkedRoot -Recurse -File -Force).Count -ne $files.Count) {
    throw 'The signed ZIP has an unexpected entry count.'
}
foreach ($file in $files) {
    $copy = Join-Path $checkedRoot ([IO.Path]::GetRelativePath($unsignedRoot, $file.FullName))
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) {
        throw "The signed ZIP changed $($file.Name)."
    }
}
$null = Assert-WindowsReleaseSignature -UnsignedPath $originalExe -SignedPath (Join-Path $checkedRoot $exeName) -CertificateSha256 $CertificateSha256
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash  $zipName" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
"zip_name=$zipName" >> $env:GITHUB_OUTPUT
"zip_sha256=$hash" >> $env:GITHUB_OUTPUT
"Sourced from $SourceCommit. SHA-256 of $zipName is $hash." >> $env:GITHUB_STEP_SUMMARY
