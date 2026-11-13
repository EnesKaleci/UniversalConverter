using System.Globalization;
using System.Resources;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Desktop;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData(null, "tr-TR", "tr-TR")]
    [InlineData(null, "tr", "tr-TR")]
    [InlineData(null, "de-DE", "en-US")]
    [InlineData(null, "", "en-US")]
    [InlineData("en-us", "tr-TR", "en-US")]
    [InlineData("tr-tr", "en-US", "tr-TR")]
    [InlineData("unsupported", "tr-TR", "tr-TR")]
    public void SelectsTheSavedOrSystemLanguage(string? saved, string system, string expected) =>
        Assert.Equal(expected, UiLocalization.ResolveLanguage(saved, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void TurkishCasingAndUtf8UseTheSelectedCulture()
    {
        var text = new UiText("tr-TR");
        var turkish = text.Culture;
        Assert.Equal("İ", "i".ToUpper(turkish));
        Assert.Equal("I", "ı".ToUpper(turkish));
        Assert.Equal("i", "İ".ToLower(turkish));
        Assert.Equal("ı", "I".ToLower(turkish));
        Assert.Equal("I", "i".ToUpper(new UiText("en-US").Culture));
        Assert.Equal("çÇğĞıİöÖşŞüÜ", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("çÇğĞıİöÖşŞüÜ")));
        Assert.Equal("Dönüştür", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text.Convert)));
    }

    [Fact]
    public void BothResourceSetsContainEveryApplicationString()
    {
        var english = UiText.Catalog.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var turkish = UiText.Catalog.GetResourceSet(CultureInfo.GetCultureInfo("tr-TR"), true, false)!;
        Assert.NotSame(english, turkish);
        var keys = english.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key).ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(turkish.GetString(key)), key));
        Assert.Equal("Genel Bakış", turkish.GetString("Dashboard"));
        Assert.Equal("Görünüm", turkish.GetString("Appearance"));
        Assert.Equal("Geçmiş", turkish.GetString("History"));
    }

    [Fact]
    public void FallsBackToEnglishForMissingTranslationAndHandlesUnknownKey()
    {
        // This fixture deliberately omits ChooseFiles from its Turkish satellite.
        var resources = new ResourceManager("UniversalConverter.Infrastructure.Tests.LocalizationFixture", typeof(LocalizationTests).Assembly);
        var text = new UiText("tr-TR", resources);
        Assert.Equal("Ayarlar", text.Settings);
        Assert.Equal("Choose files", text.ChooseFiles);
        Assert.Equal("Text unavailable.", text.Get("UnknownKey"));
    }

    [Fact]
    public async Task LanguageCoexistsWithThemeInAnExistingDatabase()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.DirectoryPath, "Önceki ayarlar", "settings.db");
        await new SettingsDatabase(path).SaveThemeAsync(ThemePreference.Dark);

        var store = new SettingsDatabase(path);
        Assert.Null(await store.LoadLanguageAsync());
        await store.SaveLanguageAsync("tr-TR");
        var reopened = new SettingsDatabase(path);
        Assert.Equal("tr-TR", await reopened.LoadLanguageAsync());
        Assert.Equal(ThemePreference.Dark, await reopened.LoadThemeAsync());
    }

    [Fact]
    public async Task ChangesEveryScreenAndExistingMessagesWithoutReplacingTheViewModel()
    {
        using var workspace = new TemporaryWorkspace();
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        await using var queue = CreateQueue(new SimulationEngine());
        var model = CreateViewModel(queue, database, workspace, "en-US");
        await model.InitializeAsync();
        model.AddFiles([Path.Combine(workspace.DirectoryPath, "girdi.txt")]);
        Assert.Contains("unsupported", model.InputMessage);

        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        model.SelectedPageIndex = 4;
        model.LanguageIndex = 1;
        Assert.Equal("Ayarlar", model.PageTitle);
        Assert.True(model.IsSettings);
        Assert.Contains("Desteklenmeyen", model.InputMessage);
        Assert.Contains(nameof(model.Text), notifications);

        var pages = new[] { "Genel Bakış", "Dönüştür", "Kuyruk", "Geçmiş", "Ayarlar", "Hakkında" };
        for (var index = 0; index < pages.Length; index++)
        {
            model.SelectedPageIndex = index;
            Assert.Equal(pages[index], model.PageTitle);
        }
        model.LanguageIndex = 0;
        Assert.Equal("About", model.PageTitle);
        Assert.Contains("unsupported", model.InputMessage);
        await model.ShutdownAsync();
        Assert.Equal("en-US", await database.LoadLanguageAsync());
    }

    [Fact]
    public async Task RestoresLanguageAndKeepsRapidSelectionsInOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        var settings = new PausedFirstLanguageWrite(database);
        await using var queue = CreateQueue(new SimulationEngine());
        var model = CreateViewModel(queue, settings, workspace, "en-US");
        await model.InitializeAsync();

        model.LanguageIndex = 1;
        Assert.Equal("tr-TR", model.Text.Culture.Name);
        model.LanguageIndex = 0;
        Assert.Equal("en-US", model.Text.Culture.Name);
        model.LanguageIndex = 1;
        Assert.Equal("tr-TR", model.Text.Culture.Name);
        Assert.Equal(new[] { "tr-TR" }, settings.Writes);
        settings.Resume.SetResult();
        await model.ShutdownAsync();
        Assert.Equal(new[] { "tr-TR", "en-US", "tr-TR" }, settings.Writes);

        await using var newQueue = CreateQueue(new SimulationEngine());
        var reopened = CreateViewModel(newQueue, database, workspace, "en-US");
        await reopened.InitializeAsync();
        Assert.Equal(1, reopened.LanguageIndex);
        Assert.Equal("Genel Bakış", reopened.PageTitle);
        await reopened.ShutdownAsync();
    }

    [Fact]
    public async Task KeepsTurkishPathsAndTranslatesAnExistingQueueAndHistoryRow()
    {
        using var workspace = new TemporaryWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "Çalışma öğeleri")).FullName;
        const string fileName = "İşık ışıĞı Şüphe.PNG";
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "fixture");
        var database = new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db"));
        var engine = new GatedEngine();
        await using var queue = CreateQueue(engine);
        var model = CreateViewModel(queue, database, workspace, "tr-TR");
        await model.InitializeAsync();

        model.AddFiles([path, path]);
        Assert.Equal(path, Assert.Single(model.InputFiles));
        Assert.Contains("yinelenen", model.InputMessage);
        await model.QueueSimulationCommand.ExecuteAsync(null);
        var acceptedPath = await engine.Input.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, acceptedPath);
        Assert.Equal(path, Assert.Single(queue.Snapshot()).Request.InputPath);
        Assert.Empty(model.InputFiles);
        model.Refresh();

        var active = Assert.Single(model.ActiveJobs);
        Assert.Equal(fileName + " → JPG", active.Title);
        Assert.Equal("İşleniyor", active.Status);
        Assert.Equal("İptal et", active.CancelLabel);
        model.LanguageIndex = 0;
        Assert.Equal(active.Title, Assert.Single(model.ActiveJobs).Title);
        Assert.Equal("Running", Assert.Single(model.ActiveJobs).Status);
        Assert.Equal("Cancel", Assert.Single(model.ActiveJobs).CancelLabel);
        active.Cancel.Execute(null);
        await model.ShutdownAsync();
        model.Refresh();
        Assert.Equal("Cancelled", Assert.Single(model.FinishedJobs).Status);
        Assert.Equal(fileName + " → JPG", Assert.Single(model.FinishedJobs).Title);
        Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
    }

    private static ConversionQueueService CreateQueue(IConverterEngine engine) =>
        new(new ConversionCoordinator(new ConversionPlanner(new ExtensionFormatDetector(SimulationEngine.Formats),
            new ConversionEngineRegistry([engine]))));

    private static MainWindowViewModel CreateViewModel(ConversionQueueService queue, ISettingsStore settings,
        TemporaryWorkspace workspace, string systemLanguage) =>
        new(queue, settings, new UiLocalization(CultureInfo.GetCultureInfo(systemLanguage)),
            new StartupLog(workspace.DirectoryPath, null), NullLogger<MainWindowViewModel>.Instance);

    private sealed class PausedFirstLanguageWrite(SettingsDatabase database) : ISettingsStore
    {
        public List<string> Writes { get; } = [];
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ThemePreference> LoadThemeAsync(CancellationToken token) => database.LoadThemeAsync(token);
        public Task SaveThemeAsync(ThemePreference theme, CancellationToken token) => database.SaveThemeAsync(theme, token);
        public Task<string?> LoadLanguageAsync(CancellationToken token) => database.LoadLanguageAsync(token);

        public async Task SaveLanguageAsync(string languageTag, CancellationToken token)
        {
            Writes.Add(languageTag);
            if (Writes.Count == 1)
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await database.SaveLanguageAsync(languageTag, token);
        }
    }

    private sealed class GatedEngine : IConverterEngine
    {
        public string Id => "test";
        public bool IsSimulation => true;
        public IReadOnlyList<ConversionCapability> Capabilities { get; } = [new(SimulationEngine.Png, SimulationEngine.Jpeg)];
        public TaskCompletionSource<string> Input { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
            CancellationToken token)
        {
            Input.SetResult(request.InputPath);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ConversionResult.Simulated();
        }
    }
}
