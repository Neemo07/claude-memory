using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
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

        // Phase 7 wires these to the Markdown preview.
        PreviewCommand = new RelayCommand(() => { }, () => false);
        OpenMarkdownCommand = new RelayCommand(() => { }, () => false);
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

    /// <summary>Reserved for Phase 7; disabled so the toolbar layout is final.</summary>
    public ICommand PreviewCommand { get; }

    /// <summary>Reserved for Phase 7; disabled so the toolbar layout is final.</summary>
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
            if (Set(ref _outputText, value ?? "")) Raise(nameof(HasOutput));
        }
    }

    public bool HasOutput => _outputText.Length > 0;

    public string Stats
    {
        get => _stats;
        private set => Set(ref _stats, value ?? "");
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
    }
}
