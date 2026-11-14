# Install the tools explicitly after reviewing the provider and distribution terms.
param([Parameter(Mandatory)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
$archiveName = 'ffmpeg-2026-11-11-git-23c4540053-essentials_build.zip'
$uri = "https://github.com/GyanD/codexffmpeg/releases/download/2026-11-11-git-23c4540053/$archiveName"
$expected = '65dcc60aff3f159d6f39a2fc285b340d6e4b4d221901f1a8955fe1ce55d77083'
$application = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$destination = Join-Path $application 'media-tools'
if (Test-Path -LiteralPath $destination) { throw 'Remove the old media-tools folder first.' }
$work = Join-Path $application ('.media-tools-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$archive = Join-Path $work $archiveName
Invoke-WebRequest -Uri $uri -OutFile $archive -UseBasicParsing
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'FFmpeg archive SHA-256 mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'archive')
$staged = Join-Path $work 'staged'
$null = New-Item -ItemType Directory -Path $staged
$hashes = foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $work 'archive') -Filter $exe -Recurse -File)
    if ($files.Count -ne 1) { throw "Unexpected number of $exe files." }
    Copy-Item -LiteralPath $files[0].FullName -Destination $staged
    $signature = Get-AuthenticodeSignature -LiteralPath $files[0].FullName
    Write-Host "$exe Authenticode $($signature.Status)"
    (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash
}
$hashes | Set-Content -LiteralPath (Join-Path $staged 'tools.sha256') -Encoding ASCII
$ffmpeg = Join-Path $staged 'ffmpeg.exe'
$ffprobe = Join-Path $staged 'ffprobe.exe'
$build = & $ffmpeg -hide_banner -buildconf 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $build -notmatch '--enable-gpl' -or $build -match '--enable-nonfree') { throw 'Unexpected FFmpeg build configuration.' }
Write-Host $build
& $ffprobe -version
if ($LASTEXITCODE -ne 0) { throw 'FFprobe version probe failed.' }
$encoders = & $ffmpeg -hide_banner -encoders | Out-String
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg encoder probe failed.' }
foreach ($encoder in 'libmp3lame','pcm_s16le','flac','aac','libvorbis','libopus','libx264','libvpx-vp9') {
    if ($encoders -notmatch "\s$encoder\s") { throw "Required encoder missing: $encoder" }
}
Move-Item -LiteralPath $staged -Destination $destination
Remove-Item -LiteralPath $archive
Write-Host "Installed verified tools in $destination. The staging directory $work contains the provider archive contents and license."
