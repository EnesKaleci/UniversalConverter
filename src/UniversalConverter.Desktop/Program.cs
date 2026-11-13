using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;

namespace UniversalConverter.Desktop;

internal static class Program
{
    private const int DiagnosticsFailed = 2;
    private const int AppControlBlocked = 3;

    internal static StartupLog Startup { get; private set; } = null!;
    internal static string DataDirectory { get; private set; } = "";

    [STAThread]
    public static int Main(string[] args)
    {
        var diagnostic = StartupChecks.IsDiagnosticMode(args);
        if (diagnostic && OperatingSystem.IsWindows())
            TryAttachConsole();

        string? fallback = null;
        try { fallback = Path.Combine(Path.GetTempPath(), "UniversalConverter", "logs"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        Exception? configurationError = null;
        try
        {
            DataDirectory = StartupChecks.ResolveDataDirectory(
                Environment.GetEnvironmentVariable("CONVERTER_DATA_DIRECTORY"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        catch (Exception ex)
        {
            configurationError = ex;
        }

        Startup = new StartupLog(configurationError is null ? Path.Combine(DataDirectory, "logs") : null, fallback);
        var assembly = typeof(Program).Assembly;
        Startup.RecordEnvironment(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
                Startup.Exception("Unhandled exception", ex);
        };

        try
        {
            if (configurationError is not null)
                throw configurationError;
            Startup.Write(diagnostic ? "Diagnostic mode started" : "Managed startup entered");
            var result = diagnostic ? RunDiagnostics() : RunDesktop(args);
            if (diagnostic)
                Console.WriteLine($"Diagnostics {(result == 0 ? "PASS" : "FAIL")}. Log: {Startup.LogPath ?? "unavailable"}");
            return result;
        }
        catch (Exception ex)
        {
            var blocked = StartupChecks.IsAppControlBlock(ex);
            Startup.Exception(blocked ? "Windows App Control block (0x800711C7)" : "Fatal startup failure", ex);
            var guidance = StartupChecks.DescribeFailure(ex);
            var message = $"Universal Converter could not start.{Environment.NewLine}{guidance}{Environment.NewLine}Log: {Startup.LogPath ?? "unavailable"}";
            try { Console.Error.WriteLine(message); }
            catch (IOException) { }
            if (!diagnostic && OperatingSystem.IsWindows())
                TryShowError(message);
            return blocked ? AppControlBlocked : 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDesktop(string[] args)
    {
        Startup.Write("Configuring Avalonia desktop");
        var result = AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        Startup.Write($"Desktop lifetime exited ({result})");
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDiagnostics()
    {
        var layout = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DeploymentLayout")?.Value ?? "development";
        Startup.Write($"Deployment: {layout}");
        var status = 0;
        if (OperatingSystem.IsWindows() && layout == "multi-file")
        {
            var missing = StartupChecks.MissingFiles(AppContext.BaseDirectory,
                ["UniversalConverter.Infrastructure.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "e_sqlite3.dll"]);
            foreach (var file in missing)
            {
                Startup.Write($"Package file missing: {file}");
                Console.WriteLine($"Package file missing: {file}. Extract the complete ZIP.");
            }
            if (missing.Count > 0)
                status = DiagnosticsFailed;
        }

        status = Math.Max(status, Check("Skia native library", ProbeSkia));
        status = Math.Max(status, Check("HarfBuzz native library", ProbeHarfBuzz));
        status = Math.Max(status, Check("SQLite settings", ProbeSqlite));
        Startup.Write(status == 0 ? "Diagnostics: PASS" : "Diagnostics: FAIL");
        return status;
    }

    private static int Check(string name, Action probe)
    {
        try
        {
            probe();
            Startup.Write($"{name}: PASS");
            Console.WriteLine($"{name}: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Startup.Exception($"{name}: FAIL", ex);
            Console.WriteLine($"{name}: FAIL. {StartupChecks.DescribeFailure(ex)}");
            return StartupChecks.IsAppControlBlock(ex) ? AppControlBlocked : DiagnosticsFailed;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeSkia()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1, 1);
        if (bitmap.GetPixels() == IntPtr.Zero)
            throw new InvalidOperationException("Skia could not allocate a test bitmap.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeHarfBuzz()
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.Add(65u, 0u);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeSqlite()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, Path.GetRandomFileName());
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        new UniversalConverter.Infrastructure.SettingsDatabase(Path.Combine(DataDirectory, "settings.db")).LoadThemeAsync().GetAwaiter().GetResult();
    }

    private static void TryAttachConsole()
    {
        try
        {
            // A GUI subsystem process has no console by default. Keep inherited redirection intact.
            if (GetFileType(GetStdHandle(-11)) is 1 or 3)
                return;
            if (AttachConsole(uint.MaxValue))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExternalException) { }
    }

    private static void TryShowError(string message)
    {
        try { MessageBoxW(IntPtr.Zero, message, "Universal Converter", 0x10); }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
