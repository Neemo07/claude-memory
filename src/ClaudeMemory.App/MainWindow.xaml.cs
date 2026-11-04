using System.ComponentModel;
using System.Windows;
using ClaudeMemory.App.ViewModels;

namespace ClaudeMemory.App;

/// <summary>
/// The main window. Code-behind is limited to view glue: it owns the view model, triggers the
/// first scan, and cancels in-flight work when the window closes (AGENTS.md UI rules).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel();
        DataContext = _model;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _model.ScanAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _model.Cancel();
}
