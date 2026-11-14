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
public sealed record JobRow(string Title, string Status, int Percent, string CancelLabel, ICommand Cancel, string Message);

public partial class MainWindowViewModel : ObservableObject
{
    private enum Page { Dashboard, Convert, Queue, History, Settings, About }

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

    private string? _languageNotice;
    private string? _themeNotice;
    private bool _settingsLoadFailed;

    internal MainWindowViewModel(ConversionQueueService queue, ISettingsStore settings, IOutputPreferences outputPreferences,
        FilePreparation preparation, UiLocalization localization,
        StartupLog startup, ILogger<MainWindowViewModel> logger)
    {
        _queue = queue;
        _settings = settings;
        _outputPreferences = outputPreferences;
        _preparation = preparation;
        _localization = localization;
        _startup = startup;
        _logger = logger;
        SelectedTarget = Targets[0];
        LanguageIndex = Text.Culture.Name == UiText.Turkish ? 1 : 0;
    }

    public UiText Text => _localization.Text;
    public IReadOnlyList<TargetOption> Targets { get; } =
        [new("JPG", SimulationEngine.Jpeg), new("PNG", SimulationEngine.Png), new("WebP", SimulationEngine.WebP)];

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
    [ObservableProperty] private bool _isPreparing;
    [ObservableProperty] private string? _outputDirectory;
    [ObservableProperty] private int _collisionIndex;
    [ObservableProperty] private IReadOnlyList<FileRow> _fileRows = [];
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
            var directory = await _outputPreferences.LoadOutputDirectoryAsync();
            OutputDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
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
        NotifyFileState();
        OnPropertyChanged(nameof(LanguageMessage));
        OnPropertyChanged(nameof(ThemeMessage));
        OnPropertyChanged(nameof(SettingsLoadMessage));
        RebuildJobRows();
    }

    [RelayCommand]
    private void OpenConvert() => SelectedPageIndex = (int)Page.Convert;

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
        OnPropertyChanged(nameof(QueueProgress));
        OnPropertyChanged(nameof(QueueProgressText));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasFinishedJobs));
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
        new RelayCommand(() => _queue.Cancel(job.Id)),
        job.Status == ConversionStatus.Failed ? Text.Get("JobFailedMessage") : "");

    public bool HasFinishedJobs => FinishedJobs.Count > 0;
    public bool HasJobs => _lastSnapshot.Count > 0;
    public int QueueProgress => HasJobs ? (int)_lastSnapshot.Average(job => job.IsTerminal ? 100 : job.Percent) : 0;
    public string QueueProgressText => Text.Format("QueueProgress", QueueProgress);

    internal async Task ShutdownAsync()
    {
        _stopping = true;
        await _fileLifetime.CancelAsync();
        await _fileWork;
        _fileLifetime.Dispose();
        await _queue.DisposeAsync();
        if (_initialization is { } initialization)
            await initialization;
        await _languageSave;
        if (SaveThemeCommand.ExecutionTask is { } save)
            await save;
    }
}
