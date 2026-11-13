using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Microsoft.Extensions.Hosting;
using UniversalConverter.Contracts;

namespace UniversalConverter.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IHost _host;
    private bool _closing;
    private bool _closed;

    public MainWindow(MainWindowViewModel viewModel, IHost host)
    {
        _viewModel = viewModel;
        _host = host;
        InitializeComponent();
        DataContext = viewModel;
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        _viewModel.ThemeChanged += ApplyTheme;
        Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closing += OnClosing;
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
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose images",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Simulation images") { Patterns = ["*.png", "*.jpg", "*.webp"] }]
            });
            _viewModel.AddFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception exception)
        {
            _viewModel.ReportPickerFailure(exception);
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
        if (items is not null)
            _viewModel.AddFiles(items.OfType<IStorageFile>().Select(file => file.TryGetLocalPath()).OfType<string>());
        e.Handled = true;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        IsEnabled = false;
        _viewModel.Stop();
        try
        {
            await _viewModel.ShutdownAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (_host is IAsyncDisposable asyncHost)
                await asyncHost.DisposeAsync();
            else
                _host.Dispose();
            _closed = true;
            Close();
        }
    }
}
