using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UniversalConverter.Desktop;

internal sealed class StartupLog
{
    private readonly object _gate = new();
    private readonly string? _fallbackDirectory;
    private readonly string _fileName = $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log";
    public string? LogPath { get; private set; }

    public StartupLog(string? primaryDirectory, string? fallbackDirectory)
    {
        _fallbackDirectory = fallbackDirectory;
        LogPath = TryCreate(primaryDirectory) ?? TryCreate(fallbackDirectory);
        Write(LogPath is null ? "logging unavailable" : "logging initialized");
    }

    public void Write(string stage)
    {
        lock (_gate)
        {
            var entry = $"{DateTimeOffset.UtcNow:O} {stage}{Environment.NewLine}";
            if (TryAppend(entry))
                return;
            LogPath = TryCreate(_fallbackDirectory);
            if (TryAppend(entry))
                return;
            TryWriteConsole(entry);
        }
    }

    public void Exception(string stage, Exception exception)
    {
        // Exception messages and source file paths can contain usernames, input paths or SQL.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            Write($"{stage}: {current.GetType().FullName} (0x{current.HResult:X8})");
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames();
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method is not null)
                    Write($"  at {method.DeclaringType?.FullName}.{method.Name}");
            }
        }
    }

    public void RecordEnvironment(string version)
    {
        Write($"Version {version}");
        Write($"OS {RuntimeInformation.OSDescription}; OS architecture {RuntimeInformation.OSArchitecture}; process architecture {RuntimeInformation.ProcessArchitecture}");
        Write($"Runtime {RuntimeInformation.FrameworkDescription}; RID {RuntimeInformation.RuntimeIdentifier}");
    }

    private string? TryCreate(string? directory)
    {
        if (directory is null)
            return null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, _fileName);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool TryAppend(string entry)
    {
        if (LogPath is null)
            return false;
        try
        {
            File.AppendAllText(LogPath, entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPath = null;
            return false;
        }
    }

    private static void TryWriteConsole(string entry)
    {
        try { Console.Error.Write(entry); }
        catch (IOException) { }
    }
}

internal static class StartupChecks
{
    public const string DiagnosticArgument = "--diagnostics";

    public static bool IsDiagnosticMode(IReadOnlyList<string> arguments) =>
        arguments.Contains(DiagnosticArgument, StringComparer.Ordinal);

    public static string ResolveDataDirectory(string? configured, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new ArgumentException("CONVERTER_DATA_DIRECTORY must be an absolute path.");
            return Path.GetFullPath(configured);
        }
        if (!Path.IsPathFullyQualified(localApplicationData))
            throw new InvalidOperationException("The user application-data directory is unavailable.");
        return Path.Combine(localApplicationData, "UniversalConverter");
    }

    private const int AppControlBlocked = unchecked((int)0x800711C7);

    public static bool IsAppControlBlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == AppControlBlocked || current is System.ComponentModel.Win32Exception { NativeErrorCode: 4551 })
                return true;
        }
        return false;
    }

    public static string DescribeFailure(Exception exception, UiText text)
    {
        if (IsAppControlBlock(exception))
            return text.Get("PolicyBlock");

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                return text.Get("NativeDependencyFailure");
            if (current is FileLoadException or FileNotFoundException)
                return text.Get("RequiredFileFailure");
        }
        return text.Format("OperationFailure", exception.GetType().Name, $"0x{exception.HResult:X8}");
    }

    public static IReadOnlyList<string> MissingFiles(string directory, IEnumerable<string> expectedFiles) =>
        expectedFiles.Where(file => !File.Exists(Path.Combine(directory, file))).ToArray();
}
