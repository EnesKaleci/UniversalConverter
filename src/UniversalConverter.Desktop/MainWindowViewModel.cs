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
public sealed partial class JobRow(Guid id, string title, ICommand cancel) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Title { get; } = title;
    public ICommand Cancel { get; } = cancel;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _percent;
    [ObservableProperty] private string _cancelLabel = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _timing = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _locationLabel = "";
    public string SourcePath { get; init; } = "";
    public ICommand? OpenLocation { get; set; }
    public bool HasOutput => OpenLocation is not null;
}

public partial class MainWindowViewModel : ObservableObject
{
    private enum Page { Dashboard, Convert, Queue, History, Settings, About }

    private readonly ConversionQueueService _queue;
    private readonly ISettingsStore _settings;
    private readonly IConversionHistory? _history;
    private IReadOnlyList<ConversionHistoryEntry> _previousHistory = [];
    private bool _historyLoadFailed;
    private readonly UiLocalization _localization;
    private readonly StartupLog _startup;
    private readonly ILogger<MainWindowViewModel> _logger;
    private IReadOnlyList<ConversionJob> _lastSnapshot = [];
    private Dictionary<Guid, JobRow> _jobRows = [];
    private Task? _initialization;
    private Task _languageSave = Task.CompletedTask;
    private int _languageSaveRevision;
    private bool _stopping;

    private string? _languageNotice;
    private string? _themeNotice;
    private bool _settingsLoadFailed;

    internal MainWindowViewModel(ConversionQueueService queue, ISettingsStore settings, IOutputPreferences outputPreferences,
        FilePreparation preparation, UiLocalization localization,
        StartupLog startup, ILogger<MainWindowViewModel> logger, IConversionHistory? history = null)
    {
        _queue = queue;
        _settings = settings;
        _history = history;
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
        [new("JPG", SimulationEngine.Jpeg), new("PNG", SimulationEngine.Png), new("WebP", SimulationEngine.WebP), new("JPEG (.jpeg)", ImageFormats.JpegLong),
         .. MediaFormats.All.Select(item => new TargetOption(item.Format.Extension.ToUpperInvariant(), item.Format))];

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
    public string QueueSummary => Text.Format("QueueSummary", ActiveJobs.Count, FinishedJobs.Count + _lastSnapshot.Count(job => job.IsTerminal && job.Request.Simulation == false));
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
    [ObservableProperty] private IReadOnlyList<JobRow> _queueResults = [];
    [ObservableProperty] private IReadOnlyList<JobRow> _imageHistory = [];

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
        if (_history is not null)
        {
            try { _previousHistory = await _history.LoadAsync(); }
            catch (Exception error)
            {
                _historyLoadFailed = true;
                _startup.Exception("Image history read failed", error);
                OnPropertyChanged(nameof(HistoryMessage));
            }
            RebuildJobRows();
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
        OnPropertyChanged(nameof(HistoryMessage));
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
        var updated = new Dictionary<Guid, JobRow>();
        foreach (var job in _lastSnapshot)
        {
            var simulation = job.Request.Simulation != false;
            if (!_jobRows.TryGetValue(job.Id, out var row))
                row = new(job.Id, Title(job.Request.InputPath, job.Request.Target.Extension), new RelayCommand(() => _queue.Cancel(job.Id)));

            row.Status = StatusText(job.Status, simulation);
            row.Percent = job.Percent;
            row.CancelLabel = Text.CancelJob;
            row.CanCancel = !job.IsTerminal && job.CanCancel;
            row.Indeterminate = !simulation && job.Status == ConversionStatus.Running && !job.HasMeasuredProgress;
            row.Mode = Text.Get(simulation ? "SimulationMode" : MediaFormats.Find(job.Request.Target) is null ? "ImageMode" : "MediaMode");
            row.Message = job.Status == ConversionStatus.Failed ? FailureText(job.Error, simulation) : "";
            updated.Add(job.Id, row);
        }
        _jobRows = updated;
        var active = _lastSnapshot.Where(job => !job.IsTerminal).Select(job => _jobRows[job.Id]).ToArray();
        var finished = _lastSnapshot.Where(job => job.IsTerminal && job.Request.Simulation != false)
            .Select(job => _jobRows[job.Id]).ToArray();
        if (!ActiveJobs.SequenceEqual(active))
            ActiveJobs = active;
        if (!FinishedJobs.SequenceEqual(finished))
            FinishedJobs = finished;
        var results = _lastSnapshot.Where(job => job.IsTerminal).Take(20).Select(job => _jobRows[job.Id]).ToArray();
        if (!QueueResults.SequenceEqual(results))
            QueueResults = results;

        var current = _lastSnapshot.Where(job => job.IsTerminal && job.Request.Simulation == false).Select(job =>
        {
            var finishedAt = job.FinishedAt!.Value;
            return new ConversionHistoryEntry(job.Id, job.Request.InputPath, job.Request.Target.Extension,
                job.Result?.OutputPath, job.Status, job.Error, finishedAt, finishedAt - (job.StartedAt ?? finishedAt));
        });
        var entries = current.Concat(_previousHistory).DistinctBy(job => job.Id)
            .OrderByDescending(job => job.FinishedAt).Take(256).ToArray();
        if (!_lastHistory.SequenceEqual(entries) || !ReferenceEquals(_historyText, Text))
        {
            _historyText = Text;
            _lastHistory = entries;
            ImageHistory = entries.Select(HistoryRow).ToArray();
        }

        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueProgress));
        OnPropertyChanged(nameof(QueueProgressText));
        OnPropertyChanged(nameof(QueueIndeterminate));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasFinishedJobs));
        OnPropertyChanged(nameof(HasSimulationHistory));
    }

    private UiText? _historyText;
    private IReadOnlyList<ConversionHistoryEntry> _lastHistory = [];

    private static string Title(string source, string target) => $"{Path.GetFileName(source)} → {target.ToUpperInvariant()}";

    private string StatusText(ConversionStatus status, bool simulation) => Text.Get(status switch
    {
        ConversionStatus.Queued => "JobQueued",
        ConversionStatus.Running => "JobRunning",
        ConversionStatus.Completed => simulation ? "JobCompleted" : "ImageCompleted",
        ConversionStatus.Failed => "JobFailed",
        ConversionStatus.Cancelled => "JobCancelled",
        ConversionStatus.Skipped => "ImageSkipped",
        _ => "TextUnavailable"
    });

    private string FailureText(string? error, bool simulation) =>
        Text.Get(!simulation && Enum.TryParse<ConversionFailure>(error, out var reason) && Enum.IsDefined(reason)
            ? "ImageError" + reason : simulation ? "JobFailedMessage" : "ImageErrorEncoding");

    private JobRow HistoryRow(ConversionHistoryEntry entry)
    {
        var row = new JobRow(entry.Id, Title(entry.SourcePath, entry.Target), new RelayCommand(() => { }))
        {
            Status = StatusText(entry.Status, false),
            Message = entry.Status == ConversionStatus.Failed ? FailureText(entry.Error, false) : "",
            SourcePath = entry.SourcePath,
            Output = entry.OutputPath ?? "",
            Timing = Text.Format("HistoryTiming", entry.FinishedAt.ToLocalTime(), Math.Max(0, entry.Duration.TotalSeconds)),
            LocationLabel = Text.OpenLocation
        };
        if (entry.Status == ConversionStatus.Completed && entry.OutputPath is { } path)
            row.OpenLocation = new AsyncRelayCommand(() => OpenLocationAsync(path));
        return row;
    }

    private async Task OpenLocationAsync(string path)
    {
        try { await Task.Run(() => OutputLocation.Open(path)); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _locationFailed = true;
            _startup.Exception("Could not open output folder", error);
            OnPropertyChanged(nameof(HistoryMessage));
        }
    }

    private bool _locationFailed;
    public string HistoryMessage => _locationFailed ? Text.Get("OpenLocationError")
        : _historyLoadFailed ? Text.Get("HistoryLoadError") : "";
    public bool HasSimulationHistory => FinishedJobs.Count > 0;
    public bool HasFinishedJobs => FinishedJobs.Count > 0 || ImageHistory.Count > 0;
    public bool HasJobs => _lastSnapshot.Count > 0;
    public bool QueueIndeterminate => _lastSnapshot.Any(job => !job.IsTerminal && job.Request.Simulation == false && !job.HasMeasuredProgress);
    public int QueueProgress => HasJobs ? (int)_lastSnapshot.Average(job => job.IsTerminal ? 100 : job.Percent) : 0;
    public string QueueProgressText => QueueIndeterminate
        ? Text.Format("QueueSettled", _lastSnapshot.Count(job => job.IsTerminal), _lastSnapshot.Count)
        : Text.Format("QueueProgress", QueueProgress);

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
