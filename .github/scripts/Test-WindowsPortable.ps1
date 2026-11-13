$ErrorActionPreference = 'Stop'

$layout = $env:PACKAGE_LAYOUT
if ($layout -notin 'multi-file', 'single-file') { throw 'Unknown package layout.' }
$stage = (Resolve-Path artifacts/publish).Path
$root = $env:PACKAGE_TEST_ROOT
$extract = Join-Path $root 'extracted'
$work = Join-Path $root 'working-directory'
New-Item -ItemType Directory -Path $extract, $work | Out-Null

Copy-Item LICENSE, README.md -Destination $stage
Copy-Item docs/legal -Destination (Join-Path $stage 'legal') -Recurse
Copy-Item docs/security/windows-app-control.md -Destination $stage
git archive --format=zip --output=artifacts/publish/UniversalConverter-source.zip HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not archive corresponding project source.' }

$signatures = Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object Extension -in '.exe', '.dll' |
    ForEach-Object {
        $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($stage, $_.FullName)
            Status = $signature.Status.ToString()
            Publisher = $signature.SignerCertificate.Subject
        }
    }
$signatures | Export-Csv (Join-Path $stage 'authenticode.csv') -NoTypeInformation
$signatures | Where-Object Path -match '^(UniversalConverter\..*|libSkiaSharp|libHarfBuzzSharp|e_sqlite3)\.(exe|dll)$' |
    ForEach-Object { Write-Host "Authenticode $($_.Path): $($_.Status)" }

$zip = Join-Path (Resolve-Path artifacts).Path "$($env:ARTIFACT_NAME).zip"
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$zipHash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Host "Portable ZIP SHA256: $zipHash"
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
$original = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force)
$extracted = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Force)
if ($original.Count -ne $extracted.Count) { throw 'ZIP entry count does not match publish output.' }
foreach ($file in $original) {
    $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
    $copy = Join-Path $extract $relative
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
        throw "Extracted file does not match publish output: $relative"
    }
}
Write-Host "Verified $($original.Count) extracted files against publish output."

$exe = Join-Path $extract 'UniversalConverter.Desktop.exe'
$infra = Join-Path $extract 'UniversalConverter.Infrastructure.dll'
$natives = @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'e_sqlite3.dll')
if ($layout -eq 'single-file') {
    if (@($extracted | Where-Object Extension -in '.dll', '.exe').Count -ne 1) {
        throw 'Single-file ZIP has unexpected sidecar binaries.'
    }
} else {
    if (-not (Test-Path -LiteralPath $infra)) { throw 'Infrastructure assembly is missing from the multi-file package.' }
    foreach ($name in $natives) {
        if (-not (Test-Path -LiteralPath (Join-Path $extract $name))) { throw "Missing native library: $name" }
    }
}

function Start-ExtractedApp {
    param([string]$Data, [string]$BundleCache, [string]$Executable = $exe, [switch]$Diagnostics)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.Environment['CONVERTER_DATA_DIRECTORY'] = $Data
    if ($layout -eq 'single-file') {
        # A fresh, runner-owned cache makes native extraction observable on each test launch.
        $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $BundleCache
    }
    if ($Diagnostics) {
        $start.ArgumentList.Add('--diagnostics')
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
    }
    [Diagnostics.Process]::Start($start)
}

function Read-StartupLog([string]$Data) {
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $Data 'logs') -Filter 'startup-*.log' -File)
    if ($logs.Count -ne 1) { throw 'Expected a single log for the test invocation.' }
    Get-Content -LiteralPath $logs[0].FullName -Raw
}

function Test-Diagnostics {
    param([string]$Data, [string]$Cache, [string]$Executable = $exe, [int]$ExpectedExit = 0)
    $process = Start-ExtractedApp -Data $Data -BundleCache $Cache -Executable $Executable -Diagnostics
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $process.WaitForExit(50)) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Diagnostics unexpectedly opened a window.' }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Diagnostics timed out.' }
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Write-Host $output
        if ($process.ExitCode -ne $ExpectedExit) { throw "Unexpected diagnostic exit: $($process.ExitCode)." }
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
    $log = Read-StartupLog $Data
    if ($log -notmatch "Deployment: $layout") { throw 'Unexpected deployment marker.' }
    if ($ExpectedExit -eq 0) {
        foreach ($marker in 'Skia native library: PASS', 'HarfBuzz native library: PASS', 'SQLite settings: PASS', 'Diagnostics: PASS') {
            if (-not $log.Contains($marker)) { throw "Diagnostic log is missing: $marker" }
        }
        if ($output -notmatch 'Diagnostics PASS\. Log:') { throw 'Missing diagnostic console summary.' }
        if (-not (Test-Path -LiteralPath (Join-Path $Data 'settings.db'))) { throw 'Diagnostics did not initialize SQLite.' }
    } elseif ($log -notmatch 'Package file missing: UniversalConverter.Infrastructure.dll' -or $log -notmatch 'Diagnostics: FAIL') {
        throw 'Incomplete-package diagnostics did not report the missing assembly.'
    }
}

$diagnosticData = Join-Path $root 'diagnostic-data'
$diagnosticCache = Join-Path $root 'diagnostic-bundle'
Test-Diagnostics -Data $diagnosticData -Cache $diagnosticCache
if ($layout -eq 'single-file') {
    $nativeCache = @(Get-ChildItem -LiteralPath $diagnosticCache -Recurse -File -Filter '*.dll')
    foreach ($name in $natives) {
        $file = $nativeCache | Where-Object Name -eq $name | Select-Object -First 1
        if (-not $file) { throw "Expected extracted native library: $name" }
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        Write-Host "Extracted native Authenticode ${name}: $($signature.Status)"
    }
    if ($nativeCache.Name -contains 'UniversalConverter.Infrastructure.dll') {
        throw 'Managed Infrastructure assembly was unexpectedly extracted.'
    }
} else {
    $incomplete = Join-Path $root 'incomplete'
    Copy-Item -LiteralPath $extract -Destination $incomplete -Recurse
    Remove-Item -LiteralPath (Join-Path $incomplete 'UniversalConverter.Infrastructure.dll')
    Test-Diagnostics -Data (Join-Path $root 'incomplete-data') -Executable (Join-Path $incomplete 'UniversalConverter.Desktop.exe') -ExpectedExit 2
}

$guiData = Join-Path $root 'gui-data'
$process = Start-ExtractedApp -Data $guiData -BundleCache (Join-Path $root 'gui-bundle')
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $initialized = $false
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Application exited before completing startup ($($process.ExitCode))." }
        $logs = @(Get-ChildItem -LiteralPath (Join-Path $guiData 'logs') -Filter 'startup-*.log' -File -ErrorAction SilentlyContinue)
        $initialized = $logs.Count -eq 1 -and (Get-Content -LiteralPath $logs[0].FullName -Raw) -match 'SQLite settings initialized'
    } until (($process.MainWindowHandle -ne [IntPtr]::Zero -and $initialized) -or [DateTime]::UtcNow -ge $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -ne 'Universal Converter') { throw 'Expected main window did not appear.' }
    if (-not $initialized -or -not (Test-Path -LiteralPath (Join-Path $guiData 'settings.db'))) { throw 'SQLite startup did not complete.' }
    if (-not $process.CloseMainWindow()) { throw 'Could not request a graceful window close.' }
    if (-not $process.WaitForExit(10000)) { throw 'Application did not shut down.' }
    if ($process.ExitCode -ne 0) { throw "Application exited with code $($process.ExitCode)." }
    $log = Read-StartupLog $guiData
    if ($log -notmatch 'Main window opened' -or $log -notmatch 'Desktop lifetime exited \(0\)') { throw 'Startup/shutdown log markers missing.' }
    Write-Host "Extracted ${layout}: main window, SQLite and graceful shutdown passed."
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
