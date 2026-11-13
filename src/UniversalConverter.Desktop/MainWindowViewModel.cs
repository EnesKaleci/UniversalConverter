using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;

namespace UniversalConverter.Desktop;

public sealed record TargetOption(string Label, FileFormat Format);
public sealed record JobRow(string Title, string Status, int Percent, ICommand Cancel);

public partial class MainWindowViewModel : ObservableObject
{
    private const int FileLimit = 128;
    private readonly ConversionQueueService _queue;
    private readonly ISettingsStore _settings;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<ConversionJob> _lastSnapshot = [];
    private Task _initialization = Task.CompletedTask;
    private bool _stopping;

    public MainWindowViewModel(ConversionQueueService queue, ISettingsStore settings, ILogger<MainWindowViewModel> logger)
    {
        _queue = queue;
        _settings = settings;
        _logger = logger;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshTimer.Tick += (_, _) => Refresh();
        SelectedTarget = Targets[0];
    }

    public IReadOnlyList<string> Pages { get; } = ["Dashboard", "Convert", "Queue", "History", "Settings", "About"];
    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public IReadOnlyList<TargetOption> Targets { get; } =
        [new("JPG", SimulationEngine.Jpeg), new("PNG", SimulationEngine.Png), new("WebP", SimulationEngine.WebP)];
    public ObservableCollection<string> InputFiles { get; } = [];
    public string Platform => $"Platform: {PlatformDetection.Current}";
    public bool IsDashboard => SelectedPage == "Dashboard";
    public bool IsConvert => SelectedPage == "Convert";
    public bool IsQueue => SelectedPage == "Queue";
    public bool IsHistory => SelectedPage == "History";
    public bool IsSettings => SelectedPage == "Settings";
    public bool IsAbout => SelectedPage == "About";
    public event Action<ThemePreference>? ThemeChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsConvert))]
    [NotifyPropertyChangedFor(nameof(IsQueue))]
    [NotifyPropertyChangedFor(nameof(IsHistory))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    [NotifyPropertyChangedFor(nameof(IsAbout))]
    private string? _selectedPage = "Dashboard";

    [ObservableProperty] private TargetOption? _selectedTarget;
    [ObservableProperty] private ThemePreference _selectedTheme;
    [ObservableProperty] private string _inputMessage = "";
    [ObservableProperty] private string _settingsMessage = "";
    [ObservableProperty] private string _queueSummary = "0 active jobs";
    [ObservableProperty] private IReadOnlyList<JobRow> _activeJobs = [];
    [ObservableProperty] private IReadOnlyList<JobRow> _finishedJobs = [];

    public Task InitializeAsync() => _initialization = LoadSettingsAsync();

    private async Task LoadSettingsAsync()
    {
        _refreshTimer.Start();
        try
        {
            SelectedTheme = await _settings.LoadThemeAsync();
            Program.Startup.Write("SQLite settings initialized");
            _logger.LogInformation("Universal Converter M0 started on {Platform}", PlatformDetection.Current);
        }
        catch (Exception exception)
        {
            Program.Startup.Exception("SQLite settings initialization failed", exception);
            SettingsMessage = "Could not load preferences. System theme is active.";
            _logger.LogWarning("Settings initialization failed ({ErrorType}).", exception.GetType().Name);
        }
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
        InputMessage = rejected == 0 ? "" : $"{rejected} unsupported, duplicate or over-limit file(s) skipped.";
    }

    public void ReportPickerFailure(Exception exception)
    {
        InputMessage = "Could not open the file picker.";
        _logger.LogWarning("File picker failed ({ErrorType}).", exception.GetType().Name);
    }

    [RelayCommand]
    private void OpenConvert() => SelectedPage = "Convert";

    [RelayCommand]
    private void ClearFiles()
    {
        InputFiles.Clear();
        InputMessage = "";
    }

    [RelayCommand]
    private async Task QueueSimulationAsync()
    {
        var target = SelectedTarget?.Format;
        var inputs = InputFiles.ToArray();
        if (_stopping || target is null || inputs.Length == 0)
        {
            InputMessage = "Choose at least one file and a target format.";
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
                catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or InvalidOperationException)
                {
                    error ??= exception.Message;
                }
            }
            return (queued, error);
        });

        foreach (var input in accepted)
            InputFiles.Remove(input);
        InputMessage = firstError is null ? $"{accepted.Count} simulation(s) queued."
            : $"{accepted.Count} queued. {inputs.Length - accepted.Count} skipped. {firstError}";
        Refresh();
        if (accepted.Count == inputs.Length)
            SelectedPage = "Queue";
    }

    partial void OnSelectedThemeChanged(ThemePreference value) => ThemeChanged?.Invoke(value);

    [RelayCommand]
    private async Task SaveThemeAsync()
    {
        try
        {
            await _initialization;
            await _settings.SaveThemeAsync(SelectedTheme);
            SettingsMessage = "Theme saved.";
        }
        catch (Exception exception)
        {
            SettingsMessage = "Could not save the theme.";
            _logger.LogWarning("Theme save failed ({ErrorType}).", exception.GetType().Name);
        }
    }

    private void Refresh()
    {
        var snapshot = _queue.Snapshot();
        if (_lastSnapshot.SequenceEqual(snapshot))
            return;

        _lastSnapshot = snapshot;
        ActiveJobs = snapshot.Where(job => !job.IsTerminal).Select(ToRow).ToArray();
        FinishedJobs = snapshot.Where(job => job.IsTerminal).Select(ToRow).ToArray();
        QueueSummary = $"{ActiveJobs.Count} active · {FinishedJobs.Count} finished";
    }

    private JobRow ToRow(ConversionJob job) => new(
        $"{Path.GetFileName(job.Request.InputPath)} → {job.Request.Target.Extension.ToUpperInvariant()}",
        job.Status switch
        {
            ConversionStatus.Completed => "Simulation complete · no file created",
            ConversionStatus.Failed => $"Failed · {job.Error}",
            _ => job.Status.ToString()
        },
        job.Percent,
        new RelayCommand(() => _queue.Cancel(job.Id)));

    public void Stop()
    {
        _stopping = true;
        _refreshTimer.Stop();
    }

    public async Task ShutdownAsync()
    {
        await _queue.DisposeAsync();
        await _initialization;
        if (SaveThemeCommand.ExecutionTask is { } save)
            await save;
        if (QueueSimulationCommand.ExecutionTask is { } enqueue)
            await enqueue;
    }
}
