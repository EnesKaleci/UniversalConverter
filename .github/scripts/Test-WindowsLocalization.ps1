$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = $env:PACKAGE_TEST_ROOT
$exe = Join-Path $root 'extracted\UniversalConverter.Desktop.exe'
$data = Join-Path $root 'localization-data'
$work = Join-Path $root 'working-directory'
$env:CONVERTER_DATA_DIRECTORY = $data
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $root 'localization-bundle'

function Wait-ForResult {
    param([scriptblock]$Find, [string]$Description)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $result = & $Find
            if ($result) { return $result }
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            # The selection or layout can change while UI Automation reads it.
        }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw "Timed out waiting for $Description."
}

function Get-UiElement {
    param($Window, [string]$Id)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-VisibleUiText {
    param($Window, [string]$Text)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Text)
    $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        Where-Object { -not $_.Current.IsOffscreen } | Select-Object -First 1
}

function Set-Selection {
    param($Window, [string]$Id)
    $item = Wait-ForResult { Get-UiElement $Window $Id } "$Id element"
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Assert-UiName {
    param($Window, [string]$Id, [string]$Name)
    Wait-ForResult {
        $item = Get-UiElement $Window $Id
        if ($item -and [string]::Equals($item.Current.Name, $Name, [StringComparison]::Ordinal)) { $true }
    } "'$Name' on $Id" | Out-Null
}

function Get-MainWindow($Process) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "Application exited early ($($Process.ExitCode))." }
    if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
        [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
    }
}

function Stop-Cleanly($Process) {
    if (-not $Process.CloseMainWindow() -or -not $Process.WaitForExit(10000) -or $Process.ExitCode -ne 0) {
        throw 'Localized window did not close cleanly.'
    }
}

function Test-Language {
    param($Window, [string]$Choice, [string[]]$Titles, [string]$Banner, [string]$Saved)
    Set-Selection $Window 'NavSettings'
    $null = Wait-ForResult {
        $picker = Get-UiElement $Window 'LanguagePicker'
        if ($picker -and $picker.Current.IsEnabled) { $true }
    } 'enabled language selector'
    Set-Selection $Window $Choice
    Assert-UiName $Window 'SimulationBanner' $Banner
    Assert-UiName $Window 'LanguageMessage' $Saved

    $pages = @('Dashboard', 'Convert', 'Queue', 'History', 'Settings', 'About')
    for ($i = 0; $i -lt $pages.Count; $i++) {
        $nav = 'Nav' + $pages[$i]
        Assert-UiName $Window $nav $Titles[$i]
        Set-Selection $Window $nav
        Assert-UiName $Window 'PageTitle' $Titles[$i]
    }
}

function Test-TurkishFile {
    param($Window, $Process)
    $folder = Join-Path $work 'Çalışma öğeleri'
    New-Item -ItemType Directory -Path $folder | Out-Null
    $file = Join-Path $folder 'İşık ışıĞı Şüphe.PNG'
    [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD2EAAAAASUVORK5CYII='))

    Set-Selection $Window 'NavConvert'
    $choose = Wait-ForResult { Get-UiElement $Window 'ChooseFiles' } 'file picker button'
    $choose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $dialog = Wait-ForResult {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $sameProcess = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
        $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $sameProcess) |
            Where-Object { $_.Current.ClassName -eq '#32770' -and $_.Current.Name -ceq 'Görselleri seç' } |
            Select-Object -First 1
    } 'Turkish file dialog'

    $entry = Wait-ForResult { Get-UiElement $dialog '1148' } 'native filename field'
    $entry.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($file)
    $open = Wait-ForResult { Get-UiElement $dialog '1' } 'native Open button'
    $open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $inputs = Wait-ForResult { Get-UiElement $Window 'InputFiles' } 'input list'
    $null = Wait-ForResult { Get-VisibleUiText $inputs $file } 'original Turkish file path'
    $submit = Get-UiElement $Window 'QueueSimulation'
    $submit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Assert-UiName $Window 'PageTitle' 'Kuyruk'
    Set-Selection $Window 'NavHistory'
    Assert-UiName $Window 'JobStatus' 'Simülasyon tamamlandı · Dosya oluşturulmadı'
    $history = Get-UiElement $Window 'FinishedJobs'
    $jobTitle = [IO.Path]::GetFileName($file) + ' → JPG'
    $null = Wait-ForResult { Get-VisibleUiText $history $jobTitle } 'original Turkish filename in History'
    if ((Get-ChildItem -LiteralPath $folder -File).Count -ne 1) { throw 'Simulation created an output file.' }
    Write-Host 'Unicode file picker, queue and history passed.'
}

# Seed the other language regardless of the runner account's display language.
$english = @('Dashboard', 'Convert', 'Queue', 'History', 'Settings', 'About')
$turkish = @('Genel Bakış', 'Dönüştür', 'Kuyruk', 'Geçmiş', 'Ayarlar', 'Hakkında')
$englishBanner = 'Simulation mode. Jobs do not create output files.'
$turkishBanner = 'Simülasyon modu. İşlemler çıktı dosyası oluşturmaz.'

$process = [Diagnostics.Process]::Start($exe, '')
try {
    $window = Wait-ForResult { Get-MainWindow $process } 'localized window'
    Set-Selection $window 'NavSettings'
    $null = Wait-ForResult {
        $picker = Get-UiElement $window 'LanguagePicker'
        if ($picker -and $picker.Current.IsEnabled) { $true }
    } 'initial language settings'
    $initial = (Get-UiElement $window 'PageTitle').Current.Name
    if ($initial -ceq 'Ayarlar') {
        Set-Selection $window 'LanguageEnglish'
        Assert-UiName $window 'LanguageMessage' 'Language preference saved.'
    }
    Test-Language $window 'LanguageTurkish' $turkish $turkishBanner 'Dil tercihi kaydedildi.'
    Test-TurkishFile $window $process
    Test-Language $window 'LanguageEnglish' $english $englishBanner 'Language preference saved.'
    Test-Language $window 'LanguageTurkish' $turkish $turkishBanner 'Dil tercihi kaydedildi.'
    Stop-Cleanly $process
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}

$process = [Diagnostics.Process]::Start($exe, '')
try {
    $window = Wait-ForResult { Get-MainWindow $process } 'reopened window'
    Assert-UiName $window 'PageTitle' 'Genel Bakış'
    Assert-UiName $window 'SimulationBanner' $turkishBanner
    Stop-Cleanly $process
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
Write-Host 'Single-file Turkish/English live switching, six screens and SQLite reload passed.'
