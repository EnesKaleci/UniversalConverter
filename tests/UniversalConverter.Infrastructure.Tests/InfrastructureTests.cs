using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task ThemePersistsAcrossDatabaseInstances()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.DirectoryPath, "prefs.db");
        var store = new SettingsDatabase(path);
        Assert.Equal(ThemePreference.System, await store.LoadThemeAsync());
        await store.SaveThemeAsync(ThemePreference.Dark);
        Assert.Equal(ThemePreference.Dark, await new SettingsDatabase(path).LoadThemeAsync());
    }

    [Fact]
    public void TempWorkspaceCreatesDistinctFilesAndCleansUp()
    {
        var workspace = new TemporaryWorkspace();
        using (workspace)
        {
            using var first = workspace.CreateFile();
            using var second = workspace.CreateFile();
            Assert.NotEqual(first.Name, second.Name);
        }
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ProcessRunnerRejectsUnregisteredExecutable()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new SafeProcessRunner([]);
        var request = new ProcessRequest(Path.Combine(workspace.DirectoryPath, "untrusted.exe"), [],
            workspace.DirectoryPath, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync(request));
    }

    [Fact]
    public async Task SimulationHonorsCancellationWithoutOutput()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var engine = new SimulationEngine();
        var input = Path.Combine(workspace.DirectoryPath, "input.png");
        File.WriteAllText(input, "test fixture");
        var result = await engine.ConvertAsync(new(input, SimulationEngine.Jpeg),
            new ProgressRecorder(cancellation), CancellationToken.None);
        Assert.True(result.IsSimulation);
        Assert.Null(result.OutputPath);
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ConvertAsync(new(input, SimulationEngine.Jpeg), new ProgressRecorder(cancellation), cancellation.Token));
    }

    private sealed class ProgressRecorder(CancellationTokenSource cancellation) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => cancellation.Cancel();
    }
}
