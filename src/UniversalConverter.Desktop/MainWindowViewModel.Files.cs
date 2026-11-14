using System.Collections;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Desktop;

public sealed record FileRow(InputFile File, string Metadata, string Preview, string? OutputPath)
{
    public string Name => File.Name;
    public string Path => File.Path;
}

public partial class MainWindowViewModel
{
    private readonly FilePreparation _preparation;
    private readonly IOutputPreferences _outputPreferences;
    private readonly CancellationTokenSource _fileLifetime = new();
    private readonly List<InputFile> _inputs = [];
    private IReadOnlyList<OutputProposal> _proposals = [];
    private IReadOnlyDictionary<InputIssue, int> _inputIssues = new Dictionary<InputIssue, int>();
    private (string Key, object[] Arguments, string? Detail)? _fileNotice;
    private Task _fileWork = Task.CompletedTask;

    public bool CanEditFiles => !_stopping && PreferencesLoaded && !IsPreparing;
    public bool HasFiles => _inputs.Count > 0;
    public bool CanQueueFiles => CanEditFiles && _proposals.Any(proposal => proposal.Path is not null);
    public bool CanConvertFiles => CanEditFiles && _proposals.Any(proposal =>
        proposal.Path is not null || proposal.Issue == OutputIssue.Conflict);
    public bool ShowsQuality => SelectedTarget is { } target && target.Format != ImageFormats.Png;
    public bool ShowsJpegWarning => (SelectedTarget?.Format == ImageFormats.Jpeg || SelectedTarget?.Format == ImageFormats.JpegLong) &&
        _inputs.Any(file => file.Format == ImageFormats.Png || file.Format == ImageFormats.WebP);

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int _quality = ImageFormats.DefaultQuality;
    public bool HasCustomFolder => OutputDirectory is not null;
    public string DestinationLabel => OutputDirectory ?? Text.Get("UseSourceFolders");
    public string FileSummary => Text.Format("FileSummary", _inputs.Count, FormatSize(_inputs.Sum(input => (decimal)input.Bytes)));
    public string InputMessage => string.Join(Environment.NewLine, NoticeLines());

    private IEnumerable<string> NoticeLines()
    {
        if (_fileNotice is { } notice)
            yield return Text.Format(notice.Key, notice.Detail is { } detail ? [.. notice.Arguments, Text.Get(detail)] : notice.Arguments);
        foreach (var (issue, count) in _inputIssues.OrderBy(entry => entry.Key))
            yield return Text.Format(issue == InputIssue.Missing ? "InputMissingCount" : "Input" + issue, count);
    }

    partial void OnPreferencesLoadedChanged(bool value) => NotifyFileState();
    partial void OnIsPreparingChanged(bool value) => NotifyFileState();
    partial void OnOutputDirectoryChanged(string? value) => NotifyFileState();

    partial void OnSelectedTargetChanged(TargetOption? value)
    {
        NotifyFileState();
        _ = RefreshPreviewAsync();
    }
    partial void OnCollisionIndexChanged(int value) => _ = RefreshPreviewAsync();

    public Task AddFilesAsync(IEnumerable<string> files) => RunFileOperationAsync(async token =>
    {
        var batch = await _preparation.InspectAsync(files.ToArray(), _inputs.ToArray(), token);
        token.ThrowIfCancellationRequested();
        _inputs.AddRange(batch.Files);
        _inputIssues = batch.Issues;
        _fileNotice = null;
        await BuildPreviewAsync(token);
    });

    public void ReportPickerFailure(Exception exception)
    {
        _fileNotice = ("PickerFailure", [], null);
        OnPropertyChanged(nameof(InputMessage));
        _startup.Exception("Storage picker failed", exception);
    }

    [RelayCommand]
    private Task RemoveSelectedFilesAsync(IList? selection)
    {
        var selected = selection?.OfType<FileRow>().Select(row => row.File).ToHashSet();
        return selected is { Count: > 0 }
            ? RunFileOperationAsync(async token =>
            {
                _inputs.RemoveAll(selected.Contains);
                ResetNotices();
                await BuildPreviewAsync(token);
            })
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearFiles()
    {
        if (!CanEditFiles)
            return;
        _inputs.Clear();
        _proposals = [];
        ResetNotices();
        NotifyFileState();
    }

    public Task SelectOutputFolderAsync(string path) => RunFileOperationAsync(async token =>
    {
        string directory;
        try { directory = await _preparation.ValidateDirectoryAsync(path, token); }
        catch (UnauthorizedAccessException)
        {
            _fileNotice = ("OutputUnwritableFolder", [], null);
            return;
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        {
            _fileNotice = ("OutputInvalidFolder", [], null);
            return;
        }
        await ChangeOutputDirectoryAsync(directory, token);
    });

    [RelayCommand]
    private Task UseSourceFoldersAsync() => RunFileOperationAsync(token => ChangeOutputDirectoryAsync(null, token));

    private async Task ChangeOutputDirectoryAsync(string? directory, CancellationToken token)
    {
        OutputDirectory = directory;
        _fileNotice = null;
        try
        {
            await _outputPreferences.SaveOutputDirectoryAsync(directory, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _fileNotice = ("OutputSaveFailed", [], null);
            _logger.LogWarning("Output folder preference could not be saved ({ErrorType}).", error.GetType().Name);
        }
        await BuildPreviewAsync(token);
    }

    [RelayCommand]
    private Task RefreshPreviewAsync() => HasFiles ? RunFileOperationAsync(token => BuildPreviewAsync(token, recheckInputs: true)) : Task.CompletedTask;

    private async Task BuildPreviewAsync(CancellationToken token, bool recheckInputs = false)
    {
        var target = SelectedTarget?.Format;
        _proposals = target is null || !Enum.IsDefined((OutputCollisionPolicy)CollisionIndex) ? []
            : await _preparation.PlanAsync(_inputs.ToArray(), target, OutputDirectory,
                (OutputCollisionPolicy)CollisionIndex, token, recheckInputs);
    }

    [RelayCommand]
    private Task QueueSimulationAsync() => QueueFilesAsync(simulation: true);

    [RelayCommand]
    private Task ConvertImagesAsync() => QueueFilesAsync(simulation: false);

    private Task QueueFilesAsync(bool simulation) => RunFileOperationAsync(async token =>
    {
        var target = SelectedTarget?.Format;
        if (target is null || !HasFiles)
            return;

        await BuildPreviewAsync(token, recheckInputs: true);
        var eligible = _proposals.Where(proposal => proposal.Path is not null ||
            !simulation && proposal.Issue == OutputIssue.Conflict).ToArray();
        var policy = (OutputCollisionPolicy)CollisionIndex;
        var quality = Quality;
        var directory = OutputDirectory;
        if (eligible.Length == 0)
        {
            _fileNotice = null;
            return;
        }
        var (queued, error) = await Task.Run(() =>
        {
            var accepted = new List<InputFile>();
            string? errorKey = null;
            foreach (var proposal in eligible)
            {
                if (token.IsCancellationRequested)
                    break;
                try
                {
                    var input = proposal.Input;
                    // Start from the original stem. The engine arbitrates the final name at commit.
                    var output = simulation ? null : Path.Combine(directory ?? Path.GetDirectoryName(input.Path)!,
                        OutputNaming.CreateFileName(input.Name, target));
                    _queue.Enqueue(new ConversionRequest(input.Path, target, output, Simulation: simulation,
                        CollisionPolicy: simulation ? null : policy, Quality: quality));
                    accepted.Add(input);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    errorKey ??= exception switch
                    {
                        FileNotFoundException => "InputMissing",
                        NotSupportedException => "PairUnsupported",
                        ArgumentException => "InvalidRequest",
                        InvalidOperationException => "QueueUnavailable",
                        _ => "InputAccessError"
                    };
                }
            }

            return (accepted, errorKey);
        }, token);

        var accepted = queued.ToHashSet();
        _inputs.RemoveAll(accepted.Contains);
        await BuildPreviewAsync(token, recheckInputs: true);
        _fileNotice = error is null ? ("QueueAccepted", [queued.Count], null) : ("QueuePartiallyAccepted", [queued.Count, eligible.Length - queued.Count], error);
        Refresh();
        if (_inputs.Count == 0 && !_stopping)
            SelectedPageIndex = (int)Page.Queue;
    });

    private Task RunFileOperationAsync(Func<CancellationToken, Task> operation) =>
        CanEditFiles ? _fileWork = RunAsync(operation) : Task.CompletedTask;

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        IsPreparing = true;
        try
        {
            await operation(_fileLifetime.Token);
        }
        catch (OperationCanceledException) when (_fileLifetime.IsCancellationRequested) { }
        finally
        {
            IsPreparing = false;
        }
    }

    private void ResetNotices()
    {
        _fileNotice = null;
        _inputIssues = new Dictionary<InputIssue, int>();
    }

    private void NotifyFileState()
    {
        var proposals = _proposals.ToDictionary(proposal => proposal.Input);
        var rows = _inputs.Select(input =>
        {
            var proposal = proposals.GetValueOrDefault(input);
            var preview = proposal?.Issue is { } issue
                ? Text.Get("Output" + issue)
                : proposal?.Path is { } path ? Text.Format("OutputPreview", Path.GetFileName(path)) : "";
            return new FileRow(input, $"{input.Format.Extension.ToUpperInvariant()} · {FormatSize(input.Bytes)}",
                preview, proposal?.Path);
        }).ToArray();
        if (!FileRows.SequenceEqual(rows))
            FileRows = rows;

        OnPropertyChanged(nameof(CanEditFiles));
        OnPropertyChanged(nameof(CanQueueFiles));
        OnPropertyChanged(nameof(CanConvertFiles));
        OnPropertyChanged(nameof(ShowsQuality));
        OnPropertyChanged(nameof(ShowsJpegWarning));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasCustomFolder));
        OnPropertyChanged(nameof(DestinationLabel));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(InputMessage));
    }

    private string FormatSize(decimal bytes) => bytes switch
    {
        >= 1L << 30 => Text.Format("SizeGiB", bytes / (decimal)(1L << 30)),
        >= 1L << 20 => Text.Format("SizeMiB", bytes / (decimal)(1L << 20)),
        >= 1L << 10 => Text.Format("SizeKiB", bytes / (decimal)(1L << 10)),
        _ => Text.Format("SizeBytes", bytes)
    };
}
