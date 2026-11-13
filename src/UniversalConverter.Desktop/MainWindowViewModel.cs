using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;

namespace UniversalConverter.Desktop;

public sealed record TargetOption(string Label, FileFormat Format);
public sealed record JobRow(string Title, string Status, int Percent, string CancelLabel, ICommand Cancel);

public partial class MainWindowViewModel : ObservableObject
{
    private enum Page { Dashboard, Convert, Queue, History, Settings, About }

    private const int FileLimit = 128;
    private readonly ConversionQueueService _queue;
    private readonly ISettingsStore _settings;
    private readonly UiLocalization _localization;
    private readonly StartupLog _startup;
    private readonly ILogger<MainWindowViewModel> _logger;
    private IReadOnlyList<ConversionJob> _lastSnapshot = [];
    private Task? _initialization;
    private Task _languageSave = Task.CompletedTask;
    private int _languageSaveRevision;
    private bool _stopping;

    private (string Key, object[] Arguments, string? DetailKey)? _inputNotice;
    private string? _languageNotice;
    private string? _themeNotice;
    private bool _settingsLoadFailed;

    internal MainWindowViewModel(ConversionQueueService queue, ISettingsStore settings, UiLocalization localization,
        StartupLog startup, ILogger<MainWindowViewModel> logger)
    {
        _queue = queue;
        _settings = settings;
        _localization = localization;
        _startup = startup;
        _logger = logger;
        SelectedTarget = Targets[0];
        LanguageIndex = Text.Culture.Name == UiText.Turkish ? 1 : 0;
    }

    public UiText Text => _localization.Text;
    public IReadOnlyList<TargetOption> Targets { get; } =
        [new("JPG", SimulationEngine.Jpeg), new("PNG", SimulationEngine.Png), new("WebP", SimulationEngine.WebP)];
    public ObservableCollection<string> InputFiles { get; } = [];

    private Page SelectedPage => (Page)SelectedPageIndex;
    public string PageTitle => Text.Get(SelectedPage.ToString());
    public bool IsDashboard => SelectedPage == Page.Dashboard;
    public bool IsConvert => SelectedPage == Page.Convert;
    public bool IsQueue => SelectedPage == Page.Queue;
    public bool IsHistory => SelectedPage == Page.History;
    public bool IsSettings => SelectedPage == Page.Settings;
    public bool IsAbout => SelectedPage == Page.About;
    public string Platform => Text.Format("PlatformLabel", PlatformDetection.Current switch
    {
        DesktopPlatform.MacOS => "macOS",
        DesktopPlatform.Unknown => Text.Get("UnknownPlatform"),
        var platform => platform.ToString()
    });
    public string QueueSummary => Text.Format("QueueSummary", ActiveJobs.Count, FinishedJobs.Count);
    public string LanguageMessage => _languageNotice is { } key ? Text.Get(key) : "";
    public string ThemeMessage => _themeNotice is { } key ? Text.Get(key) : "";
    public string SettingsLoadMessage => _settingsLoadFailed ? Text.Get("SettingsLoadError") : "";
    public string InputMessage => _inputNotice is { } notice
        ? Text.Format(notice.Key, notice.DetailKey is { } detail ? [.. notice.Arguments, Text.Get(detail)] : notice.Arguments)
        : "";

    public event Action<ThemePreference>? ThemeChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsConvert))]
    [NotifyPropertyChangedFor(nameof(IsQueue))]
    [NotifyPropertyChangedFor(nameof(IsHistory))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    [NotifyPropertyChangedFor(nameof(IsAbout))]
    private int _selectedPageIndex;

    [ObservableProperty] private TargetOption? _selectedTarget;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private bool _preferencesLoaded;
    [ObservableProperty] private IReadOnlyList<JobRow> _activeJobs = [];
    [ObservableProperty] private IReadOnlyList<JobRow> _finishedJobs = [];

    internal Task InitializeAsync() => _initialization ??= LoadSettingsAsync();

    private async Task LoadSettingsAsync()
    {
        try
        {
            ApplyLanguage(await _settings.LoadLanguageAsync());
            LanguageIndex = Text.Culture.Name == UiText.Turkish ? 1 : 0;
            ThemeIndex = (int)await _settings.LoadThemeAsync();
            _startup.Write("SQLite settings initialized");
            _logger.LogInformation("Universal Converter started on {Platform}", PlatformDetection.Current);
        }
        catch (Exception exception)
        {
            _startup.Exception("SQLite settings initialization failed", exception);
            _settingsLoadFailed = true;
            OnPropertyChanged(nameof(SettingsLoadMessage));
            _logger.LogWarning("Settings initialization failed ({ErrorType}).", exception.GetType().Name);
        }
        finally
        {
            PreferencesLoaded = true;
        }
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (!PreferencesLoaded || _stopping || value is not (0 or 1))
            return;

        var tag = value == 0 ? UiText.English : UiText.Turkish;
        ApplyLanguage(tag);
        _languageNotice = "LanguageSaving";
        OnPropertyChanged(nameof(LanguageMessage));

        // Serialize writes so an earlier selection cannot overwrite the last one.
        _languageSave = SaveLanguageAsync(_languageSave, tag, ++_languageSaveRevision);
    }

    private async Task SaveLanguageAsync(Task previousWrite, string tag, int revision)
    {
        var notice = "LanguageSaved";
        try
        {
            await previousWrite;
            await _settings.SaveLanguageAsync(tag);
        }
        catch (Exception exception)
        {
            notice = "LanguageSaveError";
            _logger.LogWarning("Language save failed ({ErrorType}).", exception.GetType().Name);
        }
        if (revision == _languageSaveRevision)
        {
            _languageNotice = notice;
            OnPropertyChanged(nameof(LanguageMessage));
        }
    }

    private void ApplyLanguage(string? tag)
    {
        var previous = Text;
        if (ReferenceEquals(previous, _localization.Apply(tag)))
            return;

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(Platform));
        OnPropertyChanged(nameof(InputMessage));
        OnPropertyChanged(nameof(LanguageMessage));
        OnPropertyChanged(nameof(ThemeMessage));
        OnPropertyChanged(nameof(SettingsLoadMessage));
        RebuildJobRows();
    }

    public void AddFiles(IEnumerable<string> files)
    {
        if (_stopping)
            return;

        var supported = SimulationEngine.Formats.Select(format => "." + format.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(InputFiles, comparer);
        var rejected = 0;
        foreach (var file in files)
        {
            if (InputFiles.Count >= FileLimit || !supported.Contains(Path.GetExtension(file)) || !seen.Add(file))
                rejected++;
            else
                InputFiles.Add(file);
        }
        SetInputNotice(rejected > 0 ? "SkippedFiles" : null, [rejected]);
    }

    public void ReportPickerFailure(Exception exception)
    {
        SetInputNotice("PickerFailure");
        _startup.Exception("File picker failed", exception);
        _logger.LogWarning("File picker failed ({ErrorType}).", exception.GetType().Name);
    }

    [RelayCommand]
    private void OpenConvert() => SelectedPageIndex = (int)Page.Convert;

    [RelayCommand]
    private void ClearFiles()
    {
        InputFiles.Clear();
        SetInputNotice(null);
    }

    [RelayCommand]
    private async Task QueueSimulationAsync()
    {
        var target = SelectedTarget?.Format;
        var inputs = InputFiles.ToArray();
        if (_stopping || target is null || inputs.Length == 0)
        {
            SetInputNotice("ChooseInputAndTarget");
            return;
        }

        var (accepted, firstError) = await Task.Run(() =>
        {
            var queued = new List<string>();
            string? error = null;
            foreach (var input in inputs)
            {
                try
                {
                    _queue.Enqueue(new ConversionRequest(input, target));
                    queued.Add(input);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    error ??= exception switch
                    {
                        FileNotFoundException => "InputMissing",
                        NotSupportedException => "PairUnsupported",
                        ArgumentException => "InvalidRequest",
                        InvalidOperationException => "QueueUnavailable",
                        _ => "InputAccessError"
                    };
                }
            }
            return (queued, error);
        });

        foreach (var input in accepted)
            InputFiles.Remove(input);
        if (firstError is null)
            SetInputNotice("QueueAccepted", [accepted.Count]);
        else
            SetInputNotice("QueuePartiallyAccepted", [accepted.Count, inputs.Length - accepted.Count], firstError);

        Refresh();
        if (accepted.Count == inputs.Length)
            SelectedPageIndex = (int)Page.Queue;
    }

    private void SetInputNotice(string? key, object[]? arguments = null, string? detailKey = null)
    {
        _inputNotice = key is null ? null : (key, arguments ?? [], detailKey);
        OnPropertyChanged(nameof(InputMessage));
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (Enum.IsDefined((ThemePreference)value))
            ThemeChanged?.Invoke((ThemePreference)value);
    }

    [RelayCommand]
    private async Task SaveThemeAsync()
    {
        if (!PreferencesLoaded || _stopping || !Enum.IsDefined((ThemePreference)ThemeIndex))
            return;

        try
        {
            await _settings.SaveThemeAsync((ThemePreference)ThemeIndex);
            _themeNotice = "ThemeSaved";
        }
        catch (Exception exception)
        {
            _themeNotice = "ThemeSaveError";
            _logger.LogWarning("Theme save failed ({ErrorType}).", exception.GetType().Name);
        }
        OnPropertyChanged(nameof(ThemeMessage));
    }

    internal void Refresh()
    {
        var snapshot = _queue.Snapshot();
        if (_lastSnapshot.SequenceEqual(snapshot))
            return;

        _lastSnapshot = snapshot;
        RebuildJobRows();
    }

    private void RebuildJobRows()
    {
        ActiveJobs = _lastSnapshot.Where(job => !job.IsTerminal).Select(ToRow).ToArray();
        FinishedJobs = _lastSnapshot.Where(job => job.IsTerminal).Select(ToRow).ToArray();
        OnPropertyChanged(nameof(QueueSummary));
    }

    private JobRow ToRow(ConversionJob job) => new(
        $"{Path.GetFileName(job.Request.InputPath)} → {job.Request.Target.Extension.ToUpperInvariant()}",
        Text.Get(job.Status switch
        {
            ConversionStatus.Queued => "JobQueued",
            ConversionStatus.Running => "JobRunning",
            ConversionStatus.Completed => "JobCompleted",
            ConversionStatus.Failed => "JobFailed",
            ConversionStatus.Cancelled => "JobCancelled",
            _ => "TextUnavailable"
        }),
        job.Percent,
        Text.CancelJob,
        new RelayCommand(() => _queue.Cancel(job.Id)));

    internal async Task ShutdownAsync()
    {
        _stopping = true;
        await _queue.DisposeAsync();
        if (_initialization is { } initialization)
            await initialization;
        await _languageSave;
        if (SaveThemeCommand.ExecutionTask is { } save)
            await save;
        if (QueueSimulationCommand.ExecutionTask is { } enqueue)
            await enqueue;
    }
}
