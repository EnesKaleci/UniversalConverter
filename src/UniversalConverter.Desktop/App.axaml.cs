using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Infrastructure;

namespace UniversalConverter.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        Program.Startup.Write("Loading application XAML and theme");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.Startup.Write("Composing desktop services");
            desktop.MainWindow = CreateMainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MainWindow CreateMainWindow()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSerilog((_, logging) =>
        {
            if (Program.Startup.LogPath is { } startupPath)
                logging.WriteTo.File(Path.Combine(Path.GetDirectoryName(startupPath)!, "app-.log"),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 2_000_000,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: 5);
        });
        builder.Services.AddSingleton(_ => new SettingsDatabase(Path.Combine(Program.DataDirectory, "settings.db")));
        builder.Services.AddSingleton<ISettingsStore>(services => services.GetRequiredService<SettingsDatabase>());
        builder.Services.AddSingleton<IOutputPreferences>(services => services.GetRequiredService<SettingsDatabase>());
        builder.Services.AddSingleton<IFileCatalog, LocalFileCatalog>();
        builder.Services.AddSingleton<FilePreparation>();
        builder.Services.AddSingleton<IConversionHistory>(services => services.GetRequiredService<SettingsDatabase>());
        builder.Services.AddSingleton<IConverterEngine, SimulationEngine>();
        builder.Services.AddSingleton<IConverterEngine, ImageConversionEngine>();
        builder.Services.AddSingleton<IConversionEngineRegistry, ConversionEngineRegistry>();
        builder.Services.AddSingleton<IFormatDetector>(_ => new ExtensionFormatDetector(SimulationEngine.Formats));
        builder.Services.AddSingleton<IConversionPlanner, ConversionPlanner>();
        builder.Services.AddSingleton<ConversionCoordinator>();
        builder.Services.AddSingleton(services => new ConversionQueueService(
            services.GetRequiredService<ConversionCoordinator>(), history: services.GetRequiredService<IConversionHistory>(),
            historyError: error => Program.Startup.Exception("Image history write failed", error)));
        builder.Services.AddSingleton(Program.Localization);
        builder.Services.AddSingleton(Program.Startup);
        builder.Services.AddSingleton(services => new MainWindowViewModel(
            services.GetRequiredService<ConversionQueueService>(),
            services.GetRequiredService<ISettingsStore>(),
            services.GetRequiredService<IOutputPreferences>(),
            services.GetRequiredService<FilePreparation>(),
            services.GetRequiredService<UiLocalization>(),
            services.GetRequiredService<StartupLog>(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MainWindowViewModel>>(),
            services.GetRequiredService<IConversionHistory>()));

        var host = builder.Build();
        try
        {
            Program.Startup.Write("Resolving main window view model");
            var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            Program.Startup.Write("Creating main window");
            var window = new MainWindow(viewModel, host);
            Program.Startup.Write("Main window created");
            return window;
        }
        catch
        {
            try
            {
                if (host is IAsyncDisposable asyncHost)
                    asyncHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                else
                    host.Dispose();
            }
            catch (Exception ex)
            {
                Program.Startup.Exception("Startup cleanup failed", ex);
            }
            throw;
        }
    }
}
