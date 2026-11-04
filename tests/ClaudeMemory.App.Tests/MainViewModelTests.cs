using System.IO;
using ClaudeMemory.App.Services;
using ClaudeMemory.App.ViewModels;

namespace ClaudeMemory.App.Tests;

public sealed class MainViewModelTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static MainViewModel NewModel(
        FakeBuildPipeline? pipeline = null,
        FakeSessionScanner? scanner = null,
        FakeFileDialogService? dialogs = null,
        FakeClipboardService? clipboard = null,
        FakeShellService? shell = null)
        => new(
            pipeline ?? new FakeBuildPipeline(),
            scanner ?? new FakeSessionScanner(),
            dialogs ?? new FakeFileDialogService(),
            clipboard ?? new FakeClipboardService(),
            shell ?? new FakeShellService(),
            () => FixedNow);

    private static FakeBuildPipeline PipelineReturning(string text)
        => new()
        {
            Handler = (_, _) => Task.FromResult(new BuildResult
            {
                Text = text,
                Stats = "1 session(s) · 0 prompt(s) · 0 tool call(s) · "
                        + text.Length + " chars ~2 tokens",
                SessionCount = 1,
            }),
        };

    [Fact]
    public void Initial_state_is_idle_with_no_output_and_the_preview_reserved()
    {
        var model = NewModel();

        Assert.False(model.IsBusy);
        Assert.False(model.HasOutput);
        Assert.Equal("", model.OutputText);
        Assert.Equal("", model.StatusMessage);
        Assert.Equal(StatusKind.Info, model.StatusKind);
        Assert.True(model.GenerateCommand.CanExecute(null));
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.SaveAsCommand.CanExecute(null));
        Assert.False(model.CopyCommand.CanExecute(null));
        Assert.False(model.CopyClaudeCommand.CanExecute(null));
        Assert.True(model.OpenFolderCommand.CanExecute(null));

        // Phase 7 placeholders must exist but stay disabled.
        Assert.False(model.PreviewCommand.CanExecute(null));
        Assert.False(model.OpenMarkdownCommand.CanExecute(null));
    }

    [Fact]
    public async Task Generate_reports_busy_then_done_and_turns_the_actions_on()
    {
        var gate = new TaskCompletionSource<BuildResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new FakeBuildPipeline { Handler = (_, _) => gate.Task };
        var model = NewModel(pipeline);

        var running = model.GenerateAsync();

        Assert.True(model.IsBusy);
        Assert.Equal("working...", model.StatusMessage);
        Assert.Equal(StatusKind.Info, model.StatusKind);
        Assert.False(model.GenerateCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.True(model.CancelCommand.CanExecute(null));

        gate.SetResult(new BuildResult { Text = "hello", Stats = "stats", SessionCount = 1 });
        await running;

        Assert.False(model.IsBusy);
        Assert.Equal("hello", model.OutputText);
        Assert.Equal("stats", model.Stats);
        Assert.Equal("done", model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
        Assert.True(model.HasOutput);
        Assert.True(model.SaveCommand.CanExecute(null));
        Assert.True(model.CopyCommand.CanExecute(null));
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Generate_shows_a_build_failure_and_keeps_the_previous_output()
    {
        var pipeline = new FakeBuildPipeline
        {
            Handler = (_, _) => throw new BuildException(
                "cannot understand the time window 'soon'. Try 30d, 12h, 30m or 2026-10-01."),
        };
        var model = NewModel(pipeline);

        await model.GenerateAsync();

        Assert.Equal(
            "cannot understand the time window 'soon'. Try 30d, 12h, 30m or 2026-10-01.",
            model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
        Assert.False(model.HasOutput);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Generate_reports_a_cancel_as_a_warning()
    {
        var pipeline = new FakeBuildPipeline
        {
            Handler = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new BuildResult { Text = "", Stats = "", SessionCount = 0 };
            },
        };
        var model = NewModel(pipeline);

        var running = model.GenerateAsync();
        model.Cancel();
        await running;

        Assert.Equal("cancelled - nothing was generated", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
        Assert.False(model.HasOutput);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Save_writes_to_the_configured_folder()
    {
        using var temp = new TempDirectory();
        var model = NewModel(PipelineReturning("hello"));

        await model.GenerateAsync();
        model.OutputFolder = temp.Path;
        model.OutputName = "MEMORY.md";
        model.Save();

        var target = Path.Combine(temp.Path, "MEMORY.md");
        Assert.True(File.Exists(target));
        Assert.Equal("hello", File.ReadAllText(target));
        Assert.Equal("saved " + target, model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
    }

    [Fact]
    public async Task Save_keeps_five_previous_versions_when_the_box_is_ticked()
    {
        using var temp = new TempDirectory();
        var model = NewModel(PipelineReturning("first"));

        await model.GenerateAsync();
        model.OutputFolder = temp.Path;
        model.OutputName = "MEMORY.md";
        model.KeepHistory = true;
        model.Save();

        var second = NewModel(PipelineReturning("second"));
        await second.GenerateAsync();
        second.OutputFolder = temp.Path;
        second.OutputName = "MEMORY.md";
        second.KeepHistory = true;
        second.Save();

        var history = Path.Combine(temp.Path, "MEMORY.history");
        Assert.True(Directory.Exists(history));
        Assert.NotEmpty(Directory.GetFiles(history));
        Assert.Equal("second", File.ReadAllText(Path.Combine(temp.Path, "MEMORY.md")));
    }

    [Fact]
    public async Task Save_refuses_a_name_that_contains_a_path()
    {
        using var temp = new TempDirectory();
        var model = NewModel(PipelineReturning("hello"));

        await model.GenerateAsync();
        model.OutputFolder = temp.Path;
        model.OutputName = "nested/MEMORY.md";
        model.Save();

        Assert.Equal("The file name must not contain a path.", model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
    }

    [Fact]
    public void Save_without_output_warns_instead_of_writing()
    {
        var model = NewModel();

        model.Save();

        Assert.Equal(
            "nothing to save yet - press Generate memory first", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    [Fact]
    public async Task Save_as_uses_the_chosen_path()
    {
        using var temp = new TempDirectory();
        var dialogs = new FakeFileDialogService
        {
            SaveResult = Path.Combine(temp.Path, "picked.md"),
        };
        var model = NewModel(PipelineReturning("body"), dialogs: dialogs);

        await model.GenerateAsync();
        model.OutputName = "MEMORY.md";
        model.SaveAs();

        var target = Path.Combine(temp.Path, "picked.md");
        Assert.True(File.Exists(target));
        Assert.Equal("body", File.ReadAllText(target));
        Assert.Equal("saved " + target, model.StatusMessage);
        Assert.Equal("MEMORY.md", dialogs.LastSuggestedName);
    }

    [Fact]
    public async Task Save_as_cancellation_is_a_warning_not_an_error()
    {
        var dialogs = new FakeFileDialogService { SaveResult = null };
        var model = NewModel(PipelineReturning("body"), dialogs: dialogs);

        await model.GenerateAsync();
        model.SaveAs();

        Assert.Equal("no file chosen - nothing changed", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    [Fact]
    public async Task Open_folder_reveals_the_resolved_folder_without_writing()
    {
        using var temp = new TempDirectory();
        var shell = new FakeShellService();
        var model = NewModel(PipelineReturning("body"), shell: shell);

        await model.GenerateAsync();
        model.OutputFolder = temp.Path;
        model.OutputName = "MEMORY.md";
        model.OpenFolder();

        Assert.Equal(temp.Path, shell.LastFolder);
        Assert.Equal("opened " + temp.Path, model.StatusMessage);
        Assert.False(File.Exists(Path.Combine(temp.Path, "MEMORY.md")));
    }

    [Fact]
    public async Task Copy_sends_the_output_and_reports_a_blocked_clipboard()
    {
        var clipboard = new FakeClipboardService();
        var model = NewModel(PipelineReturning("payload"), clipboard: clipboard);

        await model.GenerateAsync();
        model.Copy();
        Assert.Equal("payload", clipboard.LastText);
        Assert.Equal("memory copied to clipboard", model.StatusMessage);

        clipboard.Result = false;
        model.Copy();
        Assert.Equal("copy blocked - select the text manually", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    [Fact]
    public async Task Copy_for_claude_md_wraps_the_output_once()
    {
        var clipboard = new FakeClipboardService();
        var model = NewModel(PipelineReturning("body"), clipboard: clipboard);

        await model.GenerateAsync();
        model.CopyClaude();

        Assert.Equal(
            "<!-- claude-memory:begin -->\nbody\n<!-- claude-memory:end -->", clipboard.LastText);
        Assert.Equal("CLAUDE.md block copied to clipboard", model.StatusMessage);
    }

    [Fact]
    public async Task Copy_for_claude_md_does_not_double_wrap()
    {
        const string wrapped = "<!-- claude-memory:begin -->\nbody\n<!-- claude-memory:end -->";
        var clipboard = new FakeClipboardService();
        var model = NewModel(PipelineReturning(wrapped), clipboard: clipboard);

        await model.GenerateAsync();
        model.CopyClaude();

        Assert.Equal(wrapped, clipboard.LastText);
    }

    [Fact]
    public void Copy_without_output_warns()
    {
        var model = NewModel();

        model.Copy();

        Assert.Equal("nothing to copy yet - press Generate memory first", model.StatusMessage);
    }

    [Fact]
    public async Task Source_radios_are_exclusive_and_drive_the_request()
    {
        var pipeline = new FakeBuildPipeline();
        var model = NewModel(pipeline);

        Assert.Equal(MemorySource.Local, model.Source);
        Assert.True(model.SourceLocal);
        Assert.False(model.ShowShareOptions);

        model.SourceBoth = true;
        Assert.Equal(MemorySource.Both, model.Source);
        Assert.False(model.SourceLocal);
        Assert.True(model.ShowShareOptions);
        Assert.True(model.LocalFiltersEnabled);

        // Unticking the visible radio must not clear the selection.
        model.SourceLocal = false;
        Assert.Equal(MemorySource.Both, model.Source);

        model.SourceShareOnly = true;
        Assert.Equal(MemorySource.ShareOnly, model.Source);
        Assert.False(model.SourceBoth);
        Assert.False(model.LocalFiltersEnabled);

        await model.GenerateAsync();
        Assert.Equal(MemorySource.ShareOnly, pipeline.LastRequest?.Source);
    }

    [Fact]
    public async Task Build_request_maps_every_control()
    {
        var pipeline = new FakeBuildPipeline();
        var model = NewModel(pipeline);

        model.SourceBoth = true;
        model.ShareUrl = "https://claude.ai/share/abc";
        model.ShareFile = @"C:\payload.json";
        model.Here = true;
        model.Since = "12h";
        model.MaxSessions = "3";
        model.Cwd = "alpha";
        model.Mode = "full";
        model.Format = "claude-md";
        model.Lang = "both";
        model.MaxChars = "9000";
        model.MaxItems = "7";
        model.IncludeThinking = true;
        model.DropSidechains = true;
        model.AsciiOnly = true;

        await model.GenerateAsync();
        var request = pipeline.LastRequest!;

        Assert.Equal(MemorySource.Both, request.Source);
        Assert.Equal("https://claude.ai/share/abc", request.Share);
        Assert.Equal(@"C:\payload.json", request.ShareFile);
        Assert.True(request.Here);
        Assert.Equal("12h", request.Since);
        Assert.Equal("3", request.Sessions);
        Assert.Equal("alpha", request.Cwd);
        Assert.Equal("full", request.Mode);
        Assert.Equal("claude-md", request.Format);
        Assert.Equal("both", request.Lang);
        Assert.Equal("9000", request.MaxChars);
        Assert.Equal("7", request.MaxItems);
        Assert.True(request.IncludeThinking);
        Assert.True(request.DropSidechains);
        Assert.True(request.AsciiOnly);
    }

    [Fact]
    public void History_count_follows_the_checkbox()
    {
        var model = NewModel();

        model.KeepHistory = true;
        Assert.Equal(5, model.HistoryCount);

        model.KeepHistory = false;
        Assert.Equal(0, model.HistoryCount);
    }

    [Fact]
    public void Mode_hint_tracks_the_selected_kind()
    {
        var model = NewModel();

        Assert.StartsWith("Summary only", model.ModeHint);

        model.Mode = "json";
        Assert.Equal("Structured data for other tools.", model.ModeHint);
    }

    [Fact]
    public async Task Scan_lists_the_newest_discovery_entry_first()
    {
        var scanner = new FakeSessionScanner
        {
            Result = new ScanResult
            {
                Sessions = new[]
                {
                    Row("first.jsonl", "2026-01-01 10:00"),
                    Row("second.jsonl", "2026-02-02 10:00"),
                },
                Root = @"C:\home\.claude",
                ProjectsDirectory = @"C:\home\.claude\projects",
                DefaultOutputDirectory = @"C:\home\Documents",
            },
        };
        var model = NewModel(scanner: scanner);

        await model.ScanAsync();

        Assert.Equal(2, model.Sessions.Count);
        Assert.Equal("second.jsonl", model.Sessions[0].File);
        Assert.Equal("first.jsonl", model.Sessions[1].File);
        Assert.True(model.HasSessions);
        Assert.Equal(@"C:\home\.claude", model.RootLabel);
        Assert.Equal(@"C:\home\Documents", model.DefaultOutputFolder);
        Assert.Equal(1, scanner.CallCount);
    }

    [Fact]
    public async Task Scan_with_nothing_found_leaves_the_table_empty()
    {
        var model = NewModel(scanner: new FakeSessionScanner());

        await model.ScanAsync();

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.Equal("", model.StatusMessage);
    }

    [Fact]
    public async Task Browse_share_file_keeps_the_path_and_reports_a_cancel()
    {
        var dialogs = new FakeFileDialogService { OpenResult = @"C:\saved\payload.json" };
        var model = NewModel(dialogs: dialogs);

        model.BrowseShareFile();

        Assert.Equal(@"C:\saved\payload.json", model.ShareFile);
        Assert.Equal(@"selected C:\saved\payload.json", model.StatusMessage);
        Assert.Equal(DialogFilters.SharePayload, dialogs.LastFilter);
        Assert.Equal("share-payload.json", dialogs.LastSuggestedName);

        dialogs.OpenResult = null;
        model.BrowseShareFile();
        Assert.Equal("no file chosen - nothing changed", model.StatusMessage);
    }

    private static SessionRow Row(string file, string when) => new()
    {
        File = file,
        When = when,
        Folder = "~/project",
        Turns = 4,
    };
}
