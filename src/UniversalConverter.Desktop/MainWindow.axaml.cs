using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using UniversalConverter.Contracts;

namespace UniversalConverter.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _closing;
    private bool _closed;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel, IHost host) : this()
    {
        DataContext = viewModel;
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        viewModel.ThemeChanged += ApplyTheme;
        _refreshTimer.Tick += (_, _) => viewModel.Refresh();
        Opened += async (_, _) =>
        {
            Program.Startup.Write("Main window opened");
            _refreshTimer.Start();
            await viewModel.InitializeAsync();
        };
        Closing += (_, e) => OnClosing(e, viewModel, host);
    }

    private void ApplyTheme(ThemePreference theme) =>
        RequestedThemeVariant = theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

    private async void OnChooseFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = viewModel.Text.PickerTitle,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(viewModel.Text.PickerFilter) { Patterns = ["*.png", "*.jpg", "*.webp"] }]
            });
            viewModel.AddFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception exception)
        {
            viewModel.ReportPickerFailure(exception);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects &= e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is not null && DataContext is MainWindowViewModel viewModel)
            viewModel.AddFiles(items.OfType<IStorageFile>().Select(file => file.TryGetLocalPath()).OfType<string>());
        e.Handled = true;
    }

    private async void OnClosing(WindowClosingEventArgs e, MainWindowViewModel viewModel, IHost host)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        IsEnabled = false;
        _refreshTimer.Stop();
        try
        {
            await viewModel.ShutdownAsync();
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (host is IAsyncDisposable asyncHost)
                await asyncHost.DisposeAsync();
            else
                host.Dispose();
            _closed = true;
            Close();
        }
    }
}
