using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Desktop;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class FileManagementTests
{
    [Fact]
    public async Task AddsReadableFilesAndReportsUnsupportedMissingAndDuplicates()
    {
        using var workspace = new TemporaryWorkspace();
        var png = WriteFixture(workspace.DirectoryPath, "İstanbul_ışık.PNG");
        var jpg = WriteFixture(workspace.DirectoryPath, "Isparta_i.JPG");
        var unsupported = WriteFixture(workspace.DirectoryPath, "İstanbul.txt");
        var missing = Path.Combine(workspace.DirectoryPath, "Kayıp.png");
        var folder = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "Klasör.PNG")).FullName;
        var alias = Path.Combine(workspace.DirectoryPath, ".", "İstanbul_ışık.PNG");

        var batch = await Prepare().InspectAsync([png, jpg, unsupported, missing, folder, alias], []);
        Assert.Equal([png, jpg], batch.Files.Select(file => file.Path));
        Assert.Equal([3L, 3L], batch.Files.Select(file => file.Bytes));
        Assert.Equal(SimulationEngine.Png, batch.Files[0].Format);
        Assert.Equal(1, batch.Issues[InputIssue.Duplicate]);
        Assert.Equal(1, batch.Issues[InputIssue.Unsupported]);
        Assert.Equal(1, batch.Issues[InputIssue.Missing]);
        Assert.Equal(1, batch.Issues[InputIssue.AccessDenied]);
        var next = await Prepare().InspectAsync([png], batch.Files);
        Assert.Empty(next.Files);
        Assert.Equal(1, next.Issues[InputIssue.Duplicate]);
    }

    [Fact]
    public async Task ReadsAndRemovesSelectionsWithoutChangingAnySources()
    {
        using var workspace = new TemporaryWorkspace();
        var sources = new[] { "İşık.PNG", "İzmir.PNG", "Işıl.PNG", "istanbul.PNG" }
            .Select(name => WriteFixture(workspace.DirectoryPath, name)).ToArray();

        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        await using var queue = CreateQueue();
        var model = Model(queue, database, workspace.DirectoryPath);
        await model.InitializeAsync();

        Assert.False(model.HasFiles);
        Assert.False(model.CanQueueFiles);
        await model.AddFilesAsync(sources);
        Assert.Contains("4 files", model.FileSummary);
        Assert.Contains("12 B", model.FileSummary);
        await model.RemoveSelectedFilesCommand.ExecuteAsync(new ArrayList { model.FileRows[1] });
        Assert.Equal([sources[0], sources[2], sources[3]], model.FileRows.Select(row => row.Path));
        await model.RemoveSelectedFilesCommand.ExecuteAsync(new ArrayList { model.FileRows[0], model.FileRows[2] });
        Assert.Equal(sources[2], Assert.Single(model.FileRows).Path);
        model.ClearFilesCommand.Execute(null);
        Assert.Empty(model.FileRows);
        Assert.Contains("0 files", model.FileSummary);
        Assert.False(model.CanQueueFiles);
        foreach (var source in sources)
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source));
        await model.ShutdownAsync();
    }

    [Fact]
    public async Task PreviewsUnicodeNamesAndReservesNamesWithinAPlan()
    {
        using var workspace = new TemporaryWorkspace();
        var first = WriteFixture(workspace.DirectoryPath, "İstanbul Fotoğraf.PNG");
        var second = WriteFixture(workspace.DirectoryPath, "İstanbul Fotoğraf.webp");
        var existing = WriteFixture(workspace.DirectoryPath, "İstanbul Fotoğraf.jpg");
        var reservedDirectory = Path.Combine(workspace.DirectoryPath, "İstanbul Fotoğraf (1).jpg");
        Directory.CreateDirectory(reservedDirectory);
        var service = Prepare();
        var batch = await service.InspectAsync([first, second, existing], []);

        var rename = await service.PlanAsync(batch.Files, SimulationEngine.Jpeg, workspace.DirectoryPath, OutputCollisionPolicy.Rename);
        Assert.Equal("İstanbul Fotoğraf (2).jpg", Path.GetFileName(rename[0].Path));
        Assert.Equal("İstanbul Fotoğraf (3).jpg", Path.GetFileName(rename[1].Path));
        Assert.Equal(OutputIssue.PairUnsupported, rename[2].Issue);
        var skip = await service.PlanAsync(batch.Files, SimulationEngine.Jpeg, null, OutputCollisionPolicy.Skip);
        Assert.All(skip.Take(2), row => Assert.Equal(OutputIssue.Conflict, row.Issue));
        Assert.Equal(3, Directory.GetFiles(workspace.DirectoryPath).Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(existing));
    }

    [Fact]
    public async Task ValidatesTheDirectoryWithoutCreatingProbes()
    {
        using var workspace = new TemporaryWorkspace();
        var file = WriteFixture(workspace.DirectoryPath, "İşık.PNG");
        var destination = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "Çıktı Iİıi")).FullName;
        var service = Prepare();
        Assert.Equal(destination, await service.ValidateDirectoryAsync(destination + Path.DirectorySeparatorChar));
        await Assert.ThrowsAnyAsync<IOException>(() => service.ValidateDirectoryAsync(file));
        await Assert.ThrowsAnyAsync<IOException>(() => service.ValidateDirectoryAsync(Path.Combine(destination, "missing")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ValidateDirectoryAsync("relative"));
        var denied = new FilePreparation(new UnwritableCatalog(), new ExtensionFormatDetector(SimulationEngine.Formats),
            new ConversionEngineRegistry([new SimulationEngine()]));
        var inputs = (await service.InspectAsync([file], [])).Files;
        Assert.Equal(OutputIssue.UnwritableFolder, Assert.Single(await denied.PlanAsync(inputs, SimulationEngine.Jpeg,
            destination, OutputCollisionPolicy.Rename)).Issue);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task StoresAndResetsOutputDirectoryWhileKeepingThemeAndLanguage()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "İç Çıktı_ışık")).FullName;
        var path = Path.Combine(workspace.DirectoryPath, "settings.db");
        var database = new SettingsDatabase(path);
        await database.SaveThemeAsync(ThemePreference.Dark);
        await database.SaveLanguageAsync("tr-TR");
        await using var queue = CreateQueue();
        var model = Model(queue, database, workspace.DirectoryPath);
        await model.InitializeAsync();
        await model.SelectOutputFolderAsync(destination);
        Assert.Equal(destination, model.DestinationLabel);

        var reopened = new SettingsDatabase(path);
        Assert.Equal(destination, await reopened.LoadOutputDirectoryAsync());
        Assert.Equal(ThemePreference.Dark, await reopened.LoadThemeAsync());
        Assert.Equal("tr-TR", await reopened.LoadLanguageAsync());
        await model.UseSourceFoldersCommand.ExecuteAsync(null);
        Assert.False(model.HasCustomFolder);
        Assert.Equal("", await reopened.LoadOutputDirectoryAsync());
        await model.ShutdownAsync();
    }

    [Fact]
    public async Task RefreshesExistingPreviewAndNoticesInBothLanguages()
    {
        using var workspace = new TemporaryWorkspace();
        var source = WriteFixture(workspace.DirectoryPath, "İstanbul Fotoğraf.PNG");
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        await using var queue = CreateQueue();
        var model = Model(queue, database, workspace.DirectoryPath);
        await model.InitializeAsync();
        await model.AddFilesAsync([source, source]);
        Assert.Contains("Output preview", Assert.Single(model.FileRows).Preview);
        model.LanguageIndex = 1;
        Assert.Contains("Çıktı önizlemesi", Assert.Single(model.FileRows).Preview);
        Assert.Contains("yinelenen", model.InputMessage);
        model.LanguageIndex = 0;
        Assert.Contains("duplicate", model.InputMessage);
        await model.ShutdownAsync();
    }

    [Fact]
    public async Task TurkishCultureDoesNotChangeExtensionMatchingOrPathSpelling()
    {
        using var workspace = new TemporaryWorkspace();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var detector = new ExtensionFormatDetector([new FileFormat("i", FormatCategory.Image)]);
            Assert.NotNull(detector.Detect("ışık.I"));
            Assert.Null(detector.Detect("ışık.İ"));
            Assert.Null(detector.Detect("ışık.ı"));
            var source = WriteFixture(workspace.DirectoryPath, "Iİıi.PNG");
            var input = Assert.Single((await Prepare().InspectAsync([source], [])).Files);
            Assert.Equal("Iİıi.PNG", input.Name);
            Assert.Equal(source, input.Path);
            Assert.Equal("Iİıi.jpg", OutputNaming.CreateFileName(input.Name, SimulationEngine.Jpeg));
            if (OperatingSystem.IsWindows())
            {
                var alias = Path.Combine(workspace.DirectoryPath, "iİıi.png");
                Assert.Equal(1, (await Prepare().InspectAsync([alias], [input])).Issues[InputIssue.Duplicate]);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task CancelledInspectionDoesNotRun()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Prepare().InspectAsync(["unreadable.PNG"], [], new CancellationToken(true)));
    }

    [Fact]
    public async Task QueueRechecksConflictsAndLeavesUnqueuedSourcesInTheList()
    {
        using var workspace = new TemporaryWorkspace();
        var source = WriteFixture(workspace.DirectoryPath, "İstanbul.PNG");
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        await using var queue = CreateQueue();
        var model = Model(queue, database, workspace.DirectoryPath);
        await model.InitializeAsync();

        model.CollisionIndex = (int)OutputCollisionPolicy.Skip;
        await model.AddFilesAsync([source]);
        Assert.True(model.CanQueueFiles);
        var conflictingFile = WriteFixture(workspace.DirectoryPath, "İstanbul.jpg");
        await model.QueueSimulationCommand.ExecuteAsync(null);
        Assert.False(model.CanQueueFiles);
        Assert.Empty(queue.Snapshot());
        Assert.Contains("Skipped", Assert.Single(model.FileRows).Preview);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(conflictingFile));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        await model.ShutdownAsync();
    }

    [Fact]
    public async Task CancelsVisibleQueuedAndRunningJobsAtTheTwoWorkerLimit()
    {
        using var workspace = new TemporaryWorkspace();
        var inputs = new[] { "İzmir.PNG", "Işık.PNG", "Çığ.PNG" }
            .Select(name => WriteFixture(workspace.DirectoryPath, name)).ToArray();
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        var engine = new WaitingEngine();
        var detector = new ExtensionFormatDetector(SimulationEngine.Formats);
        await using var queue = new ConversionQueueService(new ConversionCoordinator(
            new ConversionPlanner(detector, new ConversionEngineRegistry([engine]))));
        var model = Model(queue, database, workspace.DirectoryPath);
        await model.InitializeAsync();
        await model.AddFilesAsync(inputs);
        await model.QueueSimulationCommand.ExecuteAsync(null);

        await engine.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Refresh();
        Assert.Equal(2, model.ActiveJobs.Count(job => job.Status == "Running"));
        var queued = Assert.Single(model.ActiveJobs.Where(job => job.Status == "Queued"));
        Assert.InRange(model.QueueProgress, 1, 99);
        queued.Cancel.Execute(null);
        var running = model.ActiveJobs.First(job => job.Status == "Running");
        running.Cancel.Execute(null);
        await engine.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.ShutdownAsync();
        model.Refresh();

        Assert.Equal(2, engine.StartedCount);
        Assert.Empty(model.ActiveJobs);
        Assert.Equal(3, model.FinishedJobs.Count);
        Assert.All(model.FinishedJobs, job => Assert.Equal("Cancelled", job.Status));
        Assert.Equal(100, model.QueueProgress);
        Assert.Equal(inputs.Order(StringComparer.Ordinal), Directory.GetFiles(workspace.DirectoryPath, "*.PNG").Order(StringComparer.Ordinal));
    }

    private sealed class WaitingEngine : IConverterEngine
    {
        private int _started;
        public string Id => "waiting";
        public bool IsSimulation => true;
        public int StartedCount => Volatile.Read(ref _started);
        public IReadOnlyList<ConversionCapability> Capabilities { get; } = [new(SimulationEngine.Png, SimulationEngine.Jpeg)];
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new ConversionProgress(20));
            if (Interlocked.Increment(ref _started) == 2)
                BothStarted.TrySetResult();
            using var cancellation = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ConversionResult.Simulated();
        }
    }

    private static string WriteFixture(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static FilePreparation Prepare() =>
        new(new LocalFileCatalog(), new ExtensionFormatDetector(SimulationEngine.Formats),
            new ConversionEngineRegistry([new SimulationEngine()]));

    private static ConversionQueueService CreateQueue() => new(new ConversionCoordinator(
        new ConversionPlanner(new ExtensionFormatDetector(SimulationEngine.Formats),
            new ConversionEngineRegistry([new SimulationEngine()]))));

    private static MainWindowViewModel Model(ConversionQueueService queue, SettingsDatabase database, string directory) =>
        new(queue, database, database, Prepare(), new UiLocalization(CultureInfo.GetCultureInfo("en-US")),
            new StartupLog(directory, null), NullLogger<MainWindowViewModel>.Instance);

    private sealed class UnwritableCatalog : IFileCatalog
    {
        public long ReadInputSize(string path) => 3;
        public bool EntryExists(string path) => false;
        public void ValidateDestination(string path) => throw new UnauthorizedAccessException();
    }
}
