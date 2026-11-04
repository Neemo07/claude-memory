using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using ClaudeMemory.App.Preview;
using ClaudeMemory.App.Services;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.App.ViewModels;

/// <summary>Severity of the status line; the view maps each value to a colour (PRD §6).</summary>
public enum StatusKind
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>
/// The window's state and actions. Every user-visible string, the button enablement rules, and
/// the built document follow <c>gui.py</c>: <c>generate</c> (728-778), <c>save</c> (780-790),
/// <c>openFolder</c> (792-798), <c>copy</c> (809-825), <c>Copy for CLAUDE.md</c> (854-861),
/// <c>pickShareFile</c> (800-807) and <c>scan</c> (461-488).
/// </summary>
/// <remarks>
/// The class deliberately references no WPF types beyond <see cref="ICommand"/>, so the state
/// machine is testable without a dispatcher or a window.
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private const string ClaudeBeginMarker = "<!-- claude-memory:begin -->";
    private const string ClaudeEndMarker = "<!-- claude-memory:end -->";

    private readonly IBuildPipeline _pipeline;
    private readonly ISessionScanner _scanner;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IShellService _shell;
    private readonly Func<DateTimeOffset> _now;

    private CancellationTokenSource? _cancellation;

    // Source
    private bool _sourceLocal = true;
    private bool _sourceBoth;
    private bool _sourceShareOnly;
    private string _shareUrl = "";
    private string _shareFile = "";

    // Which sessions
    private bool _here;
    private string _since = "30d";
    private string _maxSessions = "0";
    private string _cwd = "";

    // Output
    private string _mode = "memory";
    private string _format = "plain";
    private string _lang = "auto";
    private string _maxChars = "14000";
    private string _maxItems = "14";
    private bool _includeThinking;
    private bool _dropSidechains;
    private bool _asciiOnly;

    // Save to disk
    private string _outputFolder = "";
    private string _outputName = PathPolicy.DefaultName;
    private bool _keepHistory = true;

    // State
    private string _outputText = "";
    private string _stats = "";
    private string _statusMessage = "";
    private StatusKind _statusKind = StatusKind.Info;
    private bool _isBusy;
    private string _rootLabel = "";
    private string _defaultOutputFolder = PathPolicy.DefaultOutputDirectory();

    // Preview
    private string _previewHtml = "";
    private string _previewFileName = "";
    private bool _previewVisible;
    private bool _previewIsFile;
    private bool _previewHostAvailable = true;

    public MainViewModel(
        IBuildPipeline? pipeline = null,
        ISessionScanner? scanner = null,
        IFileDialogService? dialogs = null,
        IClipboardService? clipboard = null,
        IShellService? shell = null,
        Func<DateTimeOffset>? now = null)
    {
        _pipeline = pipeline ?? new BuildPipeline();
        _scanner = scanner ?? new SessionScanner();
        _dialogs = dialogs ?? new FileDialogService();
        _clipboard = clipboard ?? new ClipboardService();
        _shell = shell ?? new ShellService();
        _now = now ?? (() => DateTimeOffset.UtcNow);

        Sessions.CollectionChanged += OnSessionsChanged;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SaveCommand = new RelayCommand(Save, () => HasOutput && !IsBusy);
        SaveAsCommand = new RelayCommand(SaveAs, () => HasOutput && !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyCommand = new RelayCommand(Copy, () => HasOutput && !IsBusy);
        CopyClaudeCommand = new RelayCommand(CopyClaude, () => HasOutput && !IsBusy);
        BrowseShareFileCommand = new RelayCommand(BrowseShareFile, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        PreviewCommand = new RelayCommand(TogglePreview, () => !IsBusy);
        OpenMarkdownCommand = new RelayCommand(OpenMarkdownFile, () => !IsBusy);
    }

    public ObservableCollection<SessionRow> Sessions { get; } = new();

    public ICommand GenerateCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand SaveAsCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand CopyClaudeCommand { get; }

    public ICommand BrowseShareFileCommand { get; }

    public ICommand ScanCommand { get; }

    /// <summary>Toggles the Markdown preview, or opens a file when nothing is generated.</summary>
    public ICommand PreviewCommand { get; }

    /// <summary>Picks a local Markdown file and shows it in the preview.</summary>
    public ICommand OpenMarkdownCommand { get; }

    public MemorySource Source
    {
        get => _sourceShareOnly ? MemorySource.ShareOnly
            : _sourceBoth ? MemorySource.Both
            : MemorySource.Local;
    }

    public bool SourceLocal
    {
        get => _sourceLocal;
        set
        {
            if (value) SelectSource(MemorySource.Local);
        }
    }

    public bool SourceBoth
    {
        get => _sourceBoth;
        set
        {
            if (value) SelectSource(MemorySource.Both);
        }
    }

    public bool SourceShareOnly
    {
        get => _sourceShareOnly;
        set
        {
            if (value) SelectSource(MemorySource.ShareOnly);
        }
    }

    /// <summary>The share box is hidden for a local-only run (gui.py:430-443).</summary>
    public bool ShowShareOptions => Source != MemorySource.Local;

    /// <summary>"This folder only" and the window are unavailable for a share-only run.</summary>
    public bool LocalFiltersEnabled => Source != MemorySource.ShareOnly;

    public string ShareUrl
    {
        get => _shareUrl;
        set => Set(ref _shareUrl, value ?? "");
    }

    public string ShareFile
    {
        get => _shareFile;
        set => Set(ref _shareFile, value ?? "");
    }

    public bool Here
    {
        get => _here;
        set => Set(ref _here, value);
    }

    public string Since
    {
        get => _since;
        set => Set(ref _since, value ?? "");
    }

    public string MaxSessions
    {
        get => _maxSessions;
        set => Set(ref _maxSessions, value ?? "");
    }

    public string Cwd
    {
        get => _cwd;
        set => Set(ref _cwd, value ?? "");
    }

    public string Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value ?? "memory")) Raise(nameof(ModeHint));
        }
    }

    public string Format
    {
        get => _format;
        set => Set(ref _format, value ?? "plain");
    }

    public string Lang
    {
        get => _lang;
        set => Set(ref _lang, value ?? "auto");
    }

    public string MaxChars
    {
        get => _maxChars;
        set => Set(ref _maxChars, value ?? "");
    }

    public string MaxItems
    {
        get => _maxItems;
        set => Set(ref _maxItems, value ?? "");
    }

    public bool IncludeThinking
    {
        get => _includeThinking;
        set => Set(ref _includeThinking, value);
    }

    public bool DropSidechains
    {
        get => _dropSidechains;
        set => Set(ref _dropSidechains, value);
    }

    public bool AsciiOnly
    {
        get => _asciiOnly;
        set => Set(ref _asciiOnly, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => Set(ref _outputFolder, value ?? "");
    }

    public string OutputName
    {
        get => _outputName;
        set => Set(ref _outputName, value ?? PathPolicy.DefaultName);
    }

    public bool KeepHistory
    {
        get => _keepHistory;
        set => Set(ref _keepHistory, value);
    }

    public string OutputText
    {
        get => _outputText;
        private set
        {
            if (!Set(ref _outputText, value ?? "")) return;
            Raise(nameof(HasOutput));
            Raise(nameof(ShowSourceView));
            Raise(nameof(ShowEmptyState));
            Raise(nameof(PreviewCaption));
        }
    }

    public bool HasOutput => _outputText.Length > 0;

    public string Stats
    {
        get => _stats;
        private set
        {
            if (Set(ref _stats, value ?? "")) Raise(nameof(PreviewCaption));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value ?? "");
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>The transcript root, shown in the header (gui.py:464).</summary>
    public string RootLabel
    {
        get => _rootLabel;
        private set => Set(ref _rootLabel, value ?? "");
    }

    /// <summary>Shown as a hint under the folder box (gui.py:465).</summary>
    public string DefaultOutputFolder
    {
        get => _defaultOutputFolder;
        private set => Set(ref _defaultOutputFolder, value ?? "");
    }

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>True while the HTML preview replaces the source view (PRD §6).</summary>
    public bool PreviewVisible
    {
        get => _previewVisible;
        private set
        {
            if (!Set(ref _previewVisible, value)) return;
            Raise(nameof(PreviewLabel));
            Raise(nameof(ShowSourceView));
        }
    }

    /// <summary>The monospace source view shows whenever there is output and no preview.</summary>
    public bool ShowSourceView => HasOutput && !PreviewVisible;

    /// <summary>The explanatory empty state (gui.py:396-397).</summary>
    public bool ShowEmptyState => !HasOutput;

    /// <summary>The document the WebView2 host displays; empty until a preview is built.</summary>
    public string PreviewHtml
    {
        get => _previewHtml;
        private set => Set(ref _previewHtml, value ?? "");
    }

    /// <summary>File name shown instead of the stats while previewing an opened file.</summary>
    public string PreviewFileName
    {
        get => _previewFileName;
        private set
        {
            if (Set(ref _previewFileName, value ?? "")) Raise(nameof(PreviewCaption));
        }
    }

    /// <summary>True when the preview shows an opened file rather than generated output.</summary>
    public bool PreviewIsFile
    {
        get => _previewIsFile;
        private set
        {
            if (!Set(ref _previewIsFile, value)) return;
            Raise(nameof(PreviewLabel));
            Raise(nameof(PreviewCaption));
        }
    }

    /// <summary>The toolbar button text (gui.py:641-648).</summary>
    public string PreviewLabel
        => !PreviewVisible ? "Preview MD" : PreviewIsFile ? "Close preview" : "Show source";

    /// <summary>The reference's <c>#meta</c> line: the file name, or the stats (gui.py:657).</summary>
    public string PreviewCaption
        => PreviewIsFile && _previewFileName.Length > 0 ? _previewFileName : Stats;

    /// <summary>
    /// False when the WebView2 runtime is missing or failed to initialize. The source view
    /// keeps working and the preview explains how to install it (PRD §9.26).
    /// </summary>
    public bool PreviewHostAvailable
    {
        get => _previewHostAvailable;
        private set => Set(ref _previewHostAvailable, value);
    }

    /// <summary>Shown when the preview host cannot start (PRD §9.26).</summary>
    public const string PreviewUnavailableMessage =
        "The preview needs the Microsoft Edge WebView2 Runtime, which is not installed. "
        + "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and "
        + "restart claude-memory. The source view still works.";

    /// <summary>Short hint under the Kind selector (gui.py:445-454).</summary>
    public string ModeHint => Mode switch
    {
        "full" => "Whole context: the summary above plus every turn verbatim. Can be tens of "
                  + "thousands of tokens - check the size before importing it into CLAUDE.md.",
        "transcript" => "Raw readable chat, no memory framing.",
        "json" => "Structured data for other tools.",
        _ => "Summary only: decisions, rules, files, open items. Compact and cheap to read.",
    };

    /// <summary>Archived versions kept on save: 5, or 0 when the box is unticked (gui.py:751).</summary>
    public int HistoryCount => KeepHistory ? 5 : 0;

    private void SelectSource(MemorySource source)
    {
        _sourceLocal = source == MemorySource.Local;
        _sourceBoth = source == MemorySource.Both;
        _sourceShareOnly = source == MemorySource.ShareOnly;

        Raise(nameof(Source));
        Raise(nameof(SourceLocal));
        Raise(nameof(SourceBoth));
        Raise(nameof(SourceShareOnly));
        Raise(nameof(ShowShareOptions));
        Raise(nameof(LocalFiltersEnabled));
    }

    /// <summary>Runs one Generate, reporting busy, success, cancellation, or a failure.</summary>
    public async Task GenerateAsync()
    {
        if (IsBusy) return;

        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation;
        IsBusy = true;
        SetStatus("working...", StatusKind.Info);

        try
        {
            var result = await _pipeline.BuildAsync(BuildRequest(), cancellation.Token)
                .ConfigureAwait(true);
            OutputText = result.Text;
            Stats = result.Stats;

            // Reference parity: a fresh build always drops back to the source view
            // (gui.py:757-758 sets previewIsFile = false and setPreviewVisible(false)).
            PreviewIsFile = false;
            PreviewVisible = false;

            SetStatus("done", StatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            SetStatus("cancelled - nothing was generated", StatusKind.Warn);
        }
        catch (Exception error)
        {
            // The reference reports any handler failure in the status line rather than
            // crashing the window (gui.py:767-768), so user-facing build errors and
            // unexpected failures share one path.
            SetStatus(error.Message, StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
            cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Requests cancellation of an in-flight Generate (share fetch or slow disk).</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Loads the "found on this machine" table (gui.py:461-488).</summary>
    public async Task ScanAsync()
    {
        CaptureCancellation();
        IsBusy = true;
        try
        {
            var result = await Task.Run(_scanner.Scan, _cancellation?.Token ?? default)
                .ConfigureAwait(true);

            RootLabel = result.Root;
            DefaultOutputFolder = result.DefaultOutputDirectory;

            Sessions.Clear();
            // The reference shows the newest discovery entries first (gui.py:476).
            for (var index = result.Sessions.Count - 1; index >= 0; index--)
            {
                Sessions.Add(result.Sessions[index]);
            }
        }
        catch (Exception error)
        {
            SetStatus("scan failed: " + error.Message, StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Writes to the configured folder (gui.py:780-790).</summary>
    public void Save()
    {
        if (!HasOutput)
        {
            NothingYet("save");
            return;
        }

        try
        {
            var target = SaveService.Save(
                OutputName, OutputFolder, OutputText, HistoryCount, _now());
            OutputName = System.IO.Path.GetFileName(target);
            SetStatus("saved " + target, StatusKind.Ok);
        }
        catch (Exception error) when (IsSaveFailure(error))
        {
            SetStatus(error.Message, StatusKind.Error);
        }
    }

    /// <summary>Writes to a path the user picks in a native Save As dialog (gui.py:1176-1205).</summary>
    public void SaveAs()
    {
        if (!HasOutput)
        {
            NothingYet("save");
            return;
        }

        var chosen = _dialogs.PickSaveFile(OutputName, FolderOrDefault());
        if (chosen is null)
        {
            SetStatus("no file chosen - nothing changed", StatusKind.Warn);
            return;
        }

        try
        {
            var target = SaveService.SaveAs(chosen, OutputText, HistoryCount, _now());
            OutputName = System.IO.Path.GetFileName(target);
            SetStatus("saved " + target, StatusKind.Ok);
        }
        catch (Exception error) when (IsSaveFailure(error))
        {
            SetStatus(error.Message, StatusKind.Error);
        }
    }

    /// <summary>Creates and reveals the save folder (gui.py:1106-1116).</summary>
    public void OpenFolder()
    {
        try
        {
            var target = PathPolicy.ResolveSavePath(
                OutputName, OutputFolder, PathPolicy.DefaultOutputDirectory());
            var folder = SaveService.EnsureContainingFolder(target);
            _shell.RevealFolder(folder);
            SetStatus("opened " + folder, StatusKind.Ok);
        }
        catch (Exception error) when (IsSaveFailure(error))
        {
            SetStatus(error.Message, StatusKind.Error);
        }
    }

    /// <summary>Copies the generated document (gui.py:846-849).</summary>
    public void Copy()
    {
        if (!HasOutput)
        {
            NothingYet("copy");
            return;
        }

        CopyText(OutputText, "memory");
    }

    /// <summary>Copies the output wrapped in CLAUDE.md paste markers (gui.py:854-861).</summary>
    public void CopyClaude()
    {
        if (!HasOutput)
        {
            NothingYet("copy");
            return;
        }

        var body = OutputText.Trim();
        var wrapped = body.StartsWith(ClaudeBeginMarker, StringComparison.Ordinal)
            ? body
            : ClaudeBeginMarker + "\n" + body + "\n" + ClaudeEndMarker;
        CopyText(wrapped, "CLAUDE.md block");
    }

    /// <summary>Picks a saved share payload for the share-file box (gui.py:800-807).</summary>
    public void BrowseShareFile()
    {
        var chosen = _dialogs.PickOpenFile(
            "share-payload.json", FolderOrDefault(), DialogFilters.SharePayload);
        if (chosen is null)
        {
            SetStatus("no file chosen - nothing changed", StatusKind.Warn);
            return;
        }

        ShareFile = chosen;
        SetStatus("selected " + chosen, StatusKind.Ok);
    }

    /// <summary>
    /// Records whether the WebView2 host could start. Called by the window once, after
    /// initialization (PRD §9.26).
    /// </summary>
    public void SetPreviewHostAvailable(bool available) => PreviewHostAvailable = available;

    /// <summary>Toggles the preview, or opens a file when nothing has been generated.</summary>
    /// <remarks>
    /// Ported from gui.py:696-726 (<c>togglePreview</c>): hide first, then refuse JSON, then
    /// fall through to the file dialog when there is no output.
    /// </remarks>
    private void TogglePreview()
    {
        if (PreviewVisible)
        {
            var wasFile = PreviewIsFile;
            PreviewVisible = false;
            PreviewIsFile = false;
            SetStatus(wasFile ? "preview closed" : "source view", StatusKind.Ok);
            return;
        }

        if (!PreviewHostAvailable)
        {
            SetStatus(PreviewUnavailableMessage, StatusKind.Error);
            return;
        }

        if (!HasOutput)
        {
            SetStatus("nothing generated yet - choose a Markdown file", StatusKind.Warn);
            OpenMarkdownFile();
            return;
        }

        if (Mode == "json")
        {
            SetStatus(
                "JSON output is not Markdown - choose memory, full, or transcript",
                StatusKind.Warn);
            return;
        }

        try
        {
            PreviewHtml = PreviewHtmlBuilder.BuildDocument(OutputText);
            PreviewIsFile = false;
            PreviewVisible = true;
            SetStatus("Markdown preview", StatusKind.Ok);
        }
        catch (Exception error)
        {
            SetStatus("preview failed: " + error.Message, StatusKind.Error);
        }
    }

    /// <summary>Picks a local Markdown file and previews it (gui.py:674-694).</summary>
    private void OpenMarkdownFile()
    {
        if (!PreviewHostAvailable)
        {
            SetStatus(PreviewUnavailableMessage, StatusKind.Error);
            return;
        }

        var chosen = _dialogs.PickOpenFile(
            "README.md", FolderOrDefault(), DialogFilters.MarkdownPreview);
        if (chosen is null)
        {
            SetStatus("no file chosen - nothing changed", StatusKind.Warn);
            return;
        }

        OpenMarkdownPath(chosen);
    }

    /// <summary>
    /// Reads one Markdown path into the preview. Shared by the Open MD dialog, the window's
    /// drag/drop handler, and the Phase 8 command-line open.
    /// </summary>
    public void OpenMarkdownPath(string path)
    {
        if (!PreviewHostAvailable)
        {
            SetStatus(PreviewUnavailableMessage, StatusKind.Error);
            return;
        }

        try
        {
            var file = MarkdownFileReader.Read(path);
            PreviewHtml = PreviewHtmlBuilder.BuildDocument(file.Text);
            PreviewFileName = file.Name;
            PreviewIsFile = true;
            PreviewVisible = true;
            SetStatus("reading " + file.Name, StatusKind.Ok);
        }
        catch (PreviewException error)
        {
            SetStatus("cannot open Markdown file: " + error.Message, StatusKind.Error);
        }
    }

    /// <summary>Opens a validated external link, for the preview's navigation guard.</summary>
    public void OpenExternalLink(string url)
    {
        if (!PreviewUrlPolicy.IsExternal(url)) return;

        try
        {
            _shell.OpenExternal(url);
        }
        catch (Exception error)
        {
            SetStatus("could not open link: " + error.Message, StatusKind.Warn);
        }
    }

    /// <summary>Maps the current controls onto the pipeline request (gui.py:735-752).</summary>
    public BuildRequest BuildRequest() => new()
    {
        Source = Source,
        Share = ShareUrl,
        ShareFile = ShareFile,
        Here = Here,
        Since = Since,
        Sessions = MaxSessions,
        Cwd = Cwd,
        Mode = Mode,
        Format = Format,
        Lang = Lang,
        MaxChars = MaxChars,
        MaxItems = MaxItems,
        IncludeThinking = IncludeThinking,
        DropSidechains = DropSidechains,
        AsciiOnly = AsciiOnly,
    };

    public void SetStatus(string message, StatusKind kind)
    {
        StatusMessage = message;
        StatusKind = kind;
    }

    private void CopyText(string text, string label)
    {
        if (_clipboard.TrySetText(text))
        {
            SetStatus(label + " copied to clipboard", StatusKind.Ok);
            return;
        }

        SetStatus("copy blocked - select the text manually", StatusKind.Warn);
    }

    private void NothingYet(string what)
        => SetStatus("nothing to " + what + " yet - press Generate memory first", StatusKind.Warn);

    private string FolderOrDefault()
        => string.IsNullOrWhiteSpace(OutputFolder)
            ? PathPolicy.DefaultOutputDirectory()
            : OutputFolder;

    /// <summary>Starts a fresh cancellation scope when none is active.</summary>
    private void CaptureCancellation()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
    }

    private static bool IsSaveFailure(Exception error)
        => error is SaveException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Raise(nameof(HasSessions));

    protected override void Raise(string? name = null)
    {
        base.Raise(name);
        (GenerateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveAsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyClaudeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseShareFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenMarkdownCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
