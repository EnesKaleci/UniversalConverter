using System.Runtime.CompilerServices;
using UniversalConverter.Desktop;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class StartupTests
{
    [Fact]
    public void MissingConfigurationUsesUserDefault()
    {
        using var workspace = new TemporaryWorkspace();
        Assert.Equal(Path.Combine(workspace.DirectoryPath, "UniversalConverter"),
            StartupChecks.ResolveDataDirectory(null, workspace.DirectoryPath));
        Assert.Throws<InvalidOperationException>(() => StartupChecks.ResolveDataDirectory(null, ""));
    }

    [Fact]
    public void ConfigurationRequiresAbsolutePath()
    {
        using var workspace = new TemporaryWorkspace();
        Assert.Equal(workspace.DirectoryPath, StartupChecks.ResolveDataDirectory(workspace.DirectoryPath, ""));
        Assert.Throws<ArgumentException>(() => StartupChecks.ResolveDataDirectory("relative", workspace.DirectoryPath));
    }

    [Fact]
    public async Task CleanStartupInitializesSettingsAndLog()
    {
        using var workspace = new TemporaryWorkspace();
        var log = new StartupLog(Path.Combine(workspace.DirectoryPath, "logs"), null);
        await new SettingsDatabase(Path.Combine(workspace.DirectoryPath, "settings.db")).LoadThemeAsync();
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "settings.db")));
        Assert.Contains("logging initialized", File.ReadAllText(log.LogPath!));
    }

    [Fact]
    public async Task InvalidDatabaseIsReportedAndPreserved()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.DirectoryPath, "settings.db");
        var log = new StartupLog(workspace.DirectoryPath, null);
        File.WriteAllText(path, new string('X', 4096));
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => new SettingsDatabase(path).LoadThemeAsync());
        log.Exception("SQLite settings: FAIL", exception);
        Assert.Contains("SQLite settings: FAIL", File.ReadAllText(log.LogPath!));
        Assert.Equal(new string('X', 4096), File.ReadAllText(path));
    }

    [Fact]
    public async Task UnwritableSettingsPathFallsBackForLogging()
    {
        using var workspace = new TemporaryWorkspace();
        var occupied = Path.Combine(workspace.DirectoryPath, "occupied");
        File.WriteAllText(occupied, "");
        var log = new StartupLog(Path.Combine(occupied, "logs"), workspace.DirectoryPath);
        Assert.Equal(workspace.DirectoryPath, Path.GetDirectoryName(log.LogPath));
        await Assert.ThrowsAnyAsync<IOException>(() => new SettingsDatabase(Path.Combine(occupied, "settings.db")).LoadThemeAsync());
    }

    [Fact]
    public void MissingPackageFileIsReported()
    {
        using var workspace = new TemporaryWorkspace();
        File.WriteAllText(Path.Combine(workspace.DirectoryPath, "present.dll"), "");
        Assert.Equal("missing.dll", Assert.Single(StartupChecks.MissingFiles(workspace.DirectoryPath, ["present.dll", "missing.dll"])));
    }

    [Fact]
    public void ExceptionLogIncludesFramesAndClassifiesPolicyBlock()
    {
        using var workspace = new TemporaryWorkspace();
        var log = new StartupLog(workspace.DirectoryPath, null);
        var ex = Assert.Throws<PolicyBlockException>(ThrowPolicyBlock);
        Assert.True(StartupChecks.IsAppControlBlock(new TypeInitializationException("test", ex)));
        Assert.False(StartupChecks.IsAppControlBlock(new FileLoadException()));
        Assert.Contains("Windows Application Control", StartupChecks.DescribeFailure(ex));
        Assert.Contains("dependency", StartupChecks.DescribeFailure(new DllNotFoundException()));
        log.Exception("fatal", ex);
        var contents = File.ReadAllText(log.LogPath!);
        Assert.Contains("0x800711C7", contents);
        Assert.Contains(nameof(ThrowPolicyBlock), contents);
        Assert.DoesNotContain("sensitive input path", contents);
    }

    [Theory]
    [InlineData("--diagnostics", true)]
    [InlineData("--diagnostics-extra", false)]
    public void RecognizesDiagnosticMode(string argument, bool expected) =>
        Assert.Equal(expected, StartupChecks.IsDiagnosticMode([argument]));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPolicyBlock() =>
        throw new PolicyBlockException();

    private sealed class PolicyBlockException : FileLoadException
    {
        public PolicyBlockException() : base("sensitive input path") => HResult = unchecked((int)0x800711C7);
    }
}
