using System.ComponentModel;
using System.IO;
using System.Windows;
using ClaudeMemory.App.Preview;
using ClaudeMemory.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ClaudeMemory.App;

/// <summary>
/// The main window. Code-behind is limited to view glue: it owns the view model, triggers the
/// first scan, cancels in-flight work when the window closes, hosts the WebView2 preview, and
/// receives dropped Markdown files (AGENTS.md UI rules).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _navigatingToPreview;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel();
        DataContext = _model;

        Loaded += OnLoaded;
        Closing += OnClosing;
        _model.PropertyChanged += OnModelChanged;
        InitialisePreview();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _model.ScanAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _model.Cancel();

    /// <summary>
    /// Opens a file handed over by Explorer or a second launch, bringing the window forward
    /// first (PRD FR-23).
    /// </summary>
    public void OpenFromShell(string path)
    {
        Restore();
        _model.OpenFromShell(path);
    }

    /// <summary>Reports an unusable command-line argument without blocking the window.</summary>
    public void ShowStartupMessage(string message)
    {
        Restore();
        _model.ReportStartupMessage(message);
    }

    private void Restore()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>
    /// Starts WebView2 with navigation locked to the generated document (PRD §8). A missing
    /// runtime or an initialization failure leaves the source view usable and reports the
    /// install instructions in the status line instead (PRD §9.26).
    /// </summary>
    private async void InitialisePreview()
    {
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "claude-memory",
                "webview2");
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Preview.EnsureCoreWebView2Async(environment);

            var settings = Preview.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;

            Preview.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Preview.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            _model.SetPreviewHostAvailable(true);
        }
        catch (Exception)
        {
            // The preview is an enhancement; the monospace source view still works.
            Preview.Visibility = Visibility.Collapsed;
            _model.SetPreviewHostAvailable(false);
        }
    }

    /// <summary>
    /// Shows the latest built document, or clears the host when the preview closes. The flag
    /// keeps a <c>NavigationStarting</c> from our own <c>NavigateToString</c> from being treated
    /// as a user navigation.
    /// </summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.PreviewHtml)
            && e.PropertyName != nameof(MainViewModel.PreviewVisible))
        {
            return;
        }

        if (Preview.CoreWebView2 is null) return;
        if (!_model.PreviewVisible) return;
        if (_model.PreviewHtml.Length == 0) return;

        _navigatingToPreview = true;
        try
        {
            Preview.NavigateToString(_model.PreviewHtml);
        }
        finally
        {
            _navigatingToPreview = false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_navigatingToPreview) return;
        if (e.Uri.StartsWith("about:blank", StringComparison.Ordinal)) return;

        // Everything else leaves the page unless it is a plain external link.
        e.Cancel = true;
        _model.OpenExternalLink(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _model.OpenExternalLink(e.Uri);
    }

    /// <summary>Accepts only a single Markdown drop.</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = FirstMarkdownFile(e.Data) is not null;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = FirstMarkdownFile(e.Data);
        if (!string.IsNullOrEmpty(path)) _model.OpenMarkdownPath(path);
        e.Handled = true;
    }

    private static string? FirstMarkdownFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        foreach (var file in files)
        {
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }
}
