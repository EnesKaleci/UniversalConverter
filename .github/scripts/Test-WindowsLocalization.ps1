param([switch]$FileManagement, [switch]$ImageConversion, [switch]$MediaConversion)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class ConverterTestWindows
{
    private delegate bool EnumWindow(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string text,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    public static IntPtr[] VisibleWindows(int processId)
    {
        var handles = new List<IntPtr>();
        EnumWindows((window, param) =>
        {
            int owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == processId && IsWindowVisible(window))
                handles.Add(window);
            return true;
        }, IntPtr.Zero);
        return handles.ToArray();
    }

    public static IntPtr FileNameEdit(IntPtr dialog)
    {
        var edits = new List<IntPtr>();
        IntPtr fileName = IntPtr.Zero;
        EnumChildWindows(dialog, (window, param) =>
        {
            var type = new StringBuilder(128);
            GetClassName(window, type, type.Capacity);
            if (type.ToString() == "Edit" && IsWindowVisible(window))
            {
                edits.Add(window);
                for (var parent = window; parent != IntPtr.Zero && parent != dialog; parent = GetParent(parent))
                {
                    if (GetDlgCtrlID(parent) == 1148)
                    {
                        fileName = window;
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        return fileName != IntPtr.Zero ? fileName : edits.Count == 1 ? edits[0] : IntPtr.Zero;
    }

    public static bool SetFileName(IntPtr edit, string value)
    {
        const uint SetText = 0x000C;
        const uint AbortIfHung = 0x0002;
        IntPtr result;
        return SendMessageTimeout(edit, SetText, IntPtr.Zero, value, AbortIfHung, 2000, out result) != IntPtr.Zero &&
            result != IntPtr.Zero;
    }

    public static bool OpenSelectedFile(IntPtr dialog)
    {
        const int IdOk = 1;
        const uint ButtonClick = 0x00F5;
        var button = GetDlgItem(dialog, IdOk);
        return button != IntPtr.Zero && PostMessage(button, ButtonClick, IntPtr.Zero, IntPtr.Zero);
    }

    public static string Title(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
}
'@

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
    param($Window, $Process, [IntPtr]$MainHandle)
    $folder = Join-Path $work 'Çalışma öğeleri'
    New-Item -ItemType Directory -Path $folder | Out-Null
    $file = Join-Path $folder 'İşık ışıĞı Şüphe.PNG'
    [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGOQqzgBAAIWAV+8KCb8AAAAAElFTkSuQmCC'))

    $picked = $file
    if ($FileManagement) {
        $second = Join-Path $folder 'İzmir Iİıi.PNG'
        $third = Join-Path $folder 'Mavi.PNG'
        $second, $third | ForEach-Object { [IO.File]::Copy($file, $_) }
        $picked = '"' + ($file, $second, $third -join '" "') + '"'
    }

    Set-Selection $Window 'NavConvert'
    $choose = Wait-ForResult { Get-UiElement $Window 'ChooseFiles' } 'file picker button'
    $choose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    try {
        $handle = Wait-ForResult {
            [ConverterTestWindows]::VisibleWindows($Process.Id) |
                Where-Object { $_ -ne $MainHandle -and [ConverterTestWindows]::Title($_) -ceq 'Görselleri seç' } |
                Select-Object -First 1
        } 'Turkish file dialog'
    } catch {
        foreach ($candidate in [ConverterTestWindows]::VisibleWindows($Process.Id)) {
            Write-Host ("App window {0}: {1}" -f $candidate, [ConverterTestWindows]::Title($candidate))
        }
        $notice = Get-UiElement $Window 'InputMessage'
        if ($notice) { Write-Host "Picker status: $($notice.Current.Name)" }
        throw
    }
    $edit = Wait-ForResult {
        $control = [ConverterTestWindows]::FileNameEdit($handle)
        if ($control -ne [IntPtr]::Zero) { $control }
    } 'native filename edit'
    if (-not [ConverterTestWindows]::SetFileName($edit, $picked)) { throw 'Could not enter the Unicode path.' }
    if (-not [ConverterTestWindows]::OpenSelectedFile($handle)) { throw 'Could not invoke the native Open button.' }
    $null = Wait-ForResult { Get-VisibleUiText $Window $file } 'original Turkish file path'
    if ($FileManagement) {
        $size = (Get-Item -LiteralPath $file).Length
        Assert-UiName $Window 'FileSummary' "3 dosya · toplam $($size * 3) B"
        $null = Wait-ForResult { Get-VisibleUiText $Window 'Çıktı önizlemesi · İşık ışıĞı Şüphe.jpg' } 'Turkish output preview'
        $list = Get-UiElement $Window 'InputFiles'
        $itemType = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemType)
        $keep = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $file)
        $remove = @($items | Where-Object {
            -not $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $keep)
        })
        if ($items.Count -ne 3 -or $remove.Count -ne 2) { throw 'Could not locate the three file rows and original Unicode path.' }
        $remove[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $remove[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
        (Get-UiElement $Window 'RemoveSelected').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Assert-UiName $Window 'FileSummary' "1 dosya · toplam $size B"
        $null = Wait-ForResult { Get-VisibleUiText $Window $file } 'remaining source after multi-remove'
    }
    $submit = Wait-ForResult {
        $button = Get-UiElement $Window 'QueueSimulation'
        if ($button -and $button.Current.IsEnabled) { $button }
    } 'enabled queue button'
    $submit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Assert-UiName $Window 'PageTitle' 'Kuyruk'
    Set-Selection $Window 'NavHistory'
    Assert-UiName $Window 'PageTitle' 'Geçmiş'
    $jobTitle = [IO.Path]::GetFileName($file) + ' → JPG'
    try {
        $null = Wait-ForResult { Get-VisibleUiText $Window $jobTitle } 'original Turkish filename in History'
        $null = Wait-ForResult { Get-VisibleUiText $Window 'Simülasyon tamamlandı · Dosya oluşturulmadı' } 'Turkish job completion'
    } catch {
        $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name } | Select-Object -First 60 |
            ForEach-Object { Write-Host ("UIA {0}: {1} (offscreen: {2})" -f $_.Current.AutomationId, $_.Current.Name, $_.Current.IsOffscreen) }
        Get-ChildItem -LiteralPath (Join-Path $data 'logs') -File -Filter 'startup-*.log' |
            ForEach-Object { Get-Content -LiteralPath $_.FullName }
        throw
    }
    $expectedSourceCount = if ($FileManagement) { 3 } else { 1 }
    if ((Get-ChildItem -LiteralPath $folder -File).Count -ne $expectedSourceCount) { throw 'Simulation created an output file.' }
    Write-Host 'Unicode file picker, queue and history passed.'
    if ($FileManagement) { Write-Host 'M1.2 multi-file picker, output preview and multiple removal passed.' }
    if ($ImageConversion) {
        $originalHash = (Get-FileHash -LiteralPath $file).Hash
        Set-Selection $Window 'NavConvert'
        $null = Wait-ForResult { Get-UiElement $Window 'ChooseFiles' } 'real conversion file picker'
        (Get-UiElement $Window 'ChooseFiles').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $dialog = Wait-ForResult {
            [ConverterTestWindows]::VisibleWindows($Process.Id) |
                Where-Object { $_ -ne $MainHandle -and [ConverterTestWindows]::Title($_) -ceq 'Görselleri seç' } |
                Select-Object -First 1
        } 'image dialog'
        $edit = Wait-ForResult {
            $control = [ConverterTestWindows]::FileNameEdit($dialog)
            if ($control -ne [IntPtr]::Zero) { $control }
        } 'native filename edit'
        if (-not [ConverterTestWindows]::SetFileName($edit, $file) -or
            -not [ConverterTestWindows]::OpenSelectedFile($dialog)) { throw 'Could not select the real image.' }
        $button = Wait-ForResult {
            $item = Get-UiElement $Window 'ConvertImages'
            if ($item -and $item.Current.IsEnabled) { $item }
        } 'real image conversion button'
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Assert-UiName $Window 'PageTitle' 'Kuyruk'
        Set-Selection $Window 'NavHistory'
        $script:convertedImage = [IO.Path]::ChangeExtension($file, '.jpg')
        $null = Wait-ForResult { Get-VisibleUiText $Window $script:convertedImage } 'real output path in History'
        Add-Type -AssemblyName System.Drawing
        $decoded = [Drawing.Image]::FromFile($script:convertedImage)
        try {
            if ($decoded.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Jpeg.Guid -or
                $decoded.Width -ne 1 -or $decoded.Height -ne 1) { throw 'The produced file is not a 1x1 JPEG.' }
        } finally { $decoded.Dispose() }
        if ((Get-FileHash -LiteralPath $file).Hash -ne $originalHash) { throw 'Conversion changed the source.' }
        Write-Host 'M2 extracted EXE created a valid 1x1 JPEG and preserved the Unicode PNG source.'
    }
}

function Test-MediaConversion($Window, $Process) {
    $folder = Join-Path $work 'Medya örnekleri'
    $null = New-Item -ItemType Directory -Path $folder
    $tools = Join-Path (Split-Path $exe -Parent) 'media-tools'
    $ffmpeg = Join-Path $tools 'ffmpeg.exe'
    $ffprobe = Join-Path $tools 'ffprobe.exe'
    $audio = Join-Path $folder 'Çığ ve Işık.wav'
    $video = Join-Path $folder 'Şüphe Video.mp4'
    & $ffmpeg -v error -nostdin -f lavfi -i 'sine=frequency=440:duration=1' -c:a pcm_s16le $audio
    if ($LASTEXITCODE) { throw 'WAV fixture generation failed.' }
    & $ffmpeg -v error -nostdin -f lavfi -i 'testsrc2=size=64x48:rate=12:duration=1' -f lavfi -i 'sine=frequency=440:duration=1' -c:v libx264 -c:a aac -shortest $video
    if ($LASTEXITCODE) { throw 'MP4 fixture generation failed.' }
    foreach ($case in @(@($audio,'MP3','mp3','mp3'), @($video,'WEBM','webm','vp9'), @($video,'WAV','wav','pcm_s16le'))) {
        $inputPath, $target, $extension, $codec = $case
        $originalHash = (Get-FileHash -LiteralPath $inputPath).Hash
        Set-Selection $Window 'NavConvert'
        $choose = Wait-ForResult { Get-UiElement $Window 'ChooseFiles' } 'media picker'
        $choose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $dialog = Wait-ForResult {
            [ConverterTestWindows]::VisibleWindows($Process.Id) |
                Where-Object { $_ -ne $Process.MainWindowHandle -and [ConverterTestWindows]::Title($_) -ceq 'Görselleri seç' } |
                Select-Object -First 1
        } 'media file dialog'
        $edit = Wait-ForResult {
            $control = [ConverterTestWindows]::FileNameEdit($dialog)
            if ($control -ne [IntPtr]::Zero) { $control }
        } 'media filename edit'
        if (-not [ConverterTestWindows]::SetFileName($edit, $inputPath) -or
            -not [ConverterTestWindows]::OpenSelectedFile($dialog)) { throw 'Media input could not be selected.' }

        $combo = Wait-ForResult { Get-UiElement $Window 'TargetFormatPicker' } 'target format'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $option = Wait-ForResult { Get-VisibleUiText $Window $target } "$target target"
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($option)
        $pattern = $null
        if (-not $option.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern) -and
            -not $parent.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            throw "Could not select $target."
        }
        $pattern.Select()
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        $button = Wait-ForResult {
            $item = Get-UiElement $Window 'ConvertImages'
            if ($item -and $item.Current.IsEnabled) { $item }
        } 'convert button'
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Assert-UiName $Window 'PageTitle' 'Kuyruk'
        Set-Selection $Window 'NavHistory'
        $output = [IO.Path]::ChangeExtension($inputPath, $extension)
        $null = Wait-ForResult { Get-VisibleUiText $Window $output } 'verified media result in history'
        $streams = (& $ffprobe -v error -show_entries stream=codec_name -of json $output | Out-String | ConvertFrom-Json).streams
        if ($LASTEXITCODE -ne 0 -or $streams.codec_name -notcontains $codec) { throw "Missing $codec in $output." }
        if ((Get-FileHash -LiteralPath $inputPath).Hash -ne $originalHash) { throw 'The media source changed.' }
        Write-Host "Verified real EXE media conversion to $extension."
    }
}

# Seed the other language regardless of the runner account's display language.
$english = @('Dashboard', 'Convert', 'Queue', 'History', 'Settings', 'About')
$turkish = @('Genel Bakış', 'Dönüştür', 'Kuyruk', 'Geçmiş', 'Ayarlar', 'Hakkında')
$englishBanner = 'Convert PNG, JPEG and WebP. Simulation runs without creating files.'
$turkishBanner = 'PNG, JPEG ve WebP görsellerini dönüştür. Simülasyon dosya oluşturmadan çalışır.'

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
    Test-TurkishFile $window $process $process.MainWindowHandle
    if ($MediaConversion) { Test-MediaConversion $window $process }
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
    if ($ImageConversion) {
        Set-Selection $window 'NavHistory'
        $null = Wait-ForResult { Get-VisibleUiText $window $script:convertedImage } 'SQLite conversion history after restart'
        Write-Host 'M2 real conversion history survived restart.'
    }
    Stop-Cleanly $process
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
$diagnosticStart = [Diagnostics.ProcessStartInfo]::new($exe, '--diagnostics')
$diagnosticStart.UseShellExecute = $false
$diagnosticStart.RedirectStandardOutput = $true
$diagnosticStart.StandardOutputEncoding = [Text.Encoding]::UTF8
$diagnostic = [Diagnostics.Process]::Start($diagnosticStart)
try {
    $output = $diagnostic.StandardOutput.ReadToEndAsync()
    if (-not $diagnostic.WaitForExit(15000) -or $diagnostic.ExitCode -ne 0) { throw 'Localized diagnostics failed.' }
    if (-not $output.GetAwaiter().GetResult().Contains('Tanılama başarılı.')) { throw 'Turkish diagnostics text was not received intact.' }
} finally {
    if (-not $diagnostic.HasExited) { $diagnostic.Kill() }
    $diagnostic.Dispose()
}
Write-Host 'Single-file Turkish/English live switching, six screens, SQLite reload and UTF-8 diagnostics passed.'
