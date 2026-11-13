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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var data = PlatformDetection.DataDirectory;
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddSerilog((_, logging) => logging.WriteTo.File(
                Path.Combine(data, "logs", "app-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 2_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5));
            builder.Services.AddSingleton<ISettingsStore>(_ => new SettingsDatabase(Path.Combine(data, "settings.db")));
            builder.Services.AddSingleton<IConverterEngine, SimulationEngine>();
            builder.Services.AddSingleton<IConversionEngineRegistry, ConversionEngineRegistry>();
            builder.Services.AddSingleton<IFormatDetector>(_ => new ExtensionFormatDetector(SimulationEngine.Formats));
            builder.Services.AddSingleton<IConversionPlanner, ConversionPlanner>();
            builder.Services.AddSingleton<ConversionCoordinator>();
            builder.Services.AddSingleton<ConversionQueueService>();
            builder.Services.AddSingleton<MainWindowViewModel>();

            var host = builder.Build();
            desktop.MainWindow = new MainWindow(host.Services.GetRequiredService<MainWindowViewModel>(), host);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
