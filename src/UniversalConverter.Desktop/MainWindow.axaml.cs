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
    private bool _pickerOpen;

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
        if (DataContext is not MainWindowViewModel { CanEditFiles: true } viewModel || _pickerOpen)
            return;
        _pickerOpen = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = viewModel.Text.PickerTitle,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(viewModel.Text.PickerFilter) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
            });
            if (files.Count > 0)
            {
                await viewModel.AddFilesAsync(files.Select(file => file.TryGetLocalPath() ?? ""));
                if (viewModel.IsConvert && viewModel.HasFiles && !_closing)
                    InputList.Focus();
            }
        }
        catch (Exception exception)
        {
            viewModel.ReportPickerFailure(exception);
        }
        finally { _pickerOpen = false; }
    }

    private async void OnChooseOutputFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { CanEditFiles: true } viewModel || _pickerOpen)
            return;

        _pickerOpen = true;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = viewModel.Text.ChooseOutputFolder,
                AllowMultiple = false
            });
            if (folders.Count > 0)
                await viewModel.SelectOutputFolderAsync(folders[0].TryGetLocalPath() ?? "");
        }
        catch (Exception exception)
        {
            viewModel.ReportPickerFailure(exception);
        }
        finally { _pickerOpen = false; }
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RemoveSelectedButton is not null)
            RemoveSelectedButton.IsEnabled = InputList.SelectedItems is { Count: > 0 };
    }

    private async void OnRemoveSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RemoveSelectionAsync();

    private async void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;

        e.Handled = true;
        await RemoveSelectionAsync();
    }

    private async Task RemoveSelectionAsync()
    {
        if (DataContext is MainWindowViewModel { CanEditFiles: true } viewModel)
        {
            await viewModel.RemoveSelectedFilesCommand.ExecuteAsync(InputList.SelectedItems);
            if (_closing)
                return;
            if (viewModel.HasFiles)
                InputList.Focus();
            else
                ChooseFilesButton.Focus();
        }
    }

    private void OnClearFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanEditFiles: true } viewModel)
        {
            viewModel.ClearFilesCommand.Execute(null);
            ChooseFilesButton.Focus();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects &= DataContext is MainWindowViewModel { CanEditFiles: true } && !_pickerOpen &&
            e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        e.Handled = true;
        if (items is not null && !_pickerOpen && DataContext is MainWindowViewModel { CanEditFiles: true } viewModel)
            await viewModel.AddFilesAsync(items.Select(file => file.TryGetLocalPath() ?? ""));
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
