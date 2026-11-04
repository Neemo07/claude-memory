using System.IO;
using ClaudeMemory.App.Preview;
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
        FakeShellService? shell = null,
        FakeAssociationService? association = null)
        => new(
            pipeline ?? new FakeBuildPipeline(),
            scanner ?? new FakeSessionScanner(),
            dialogs ?? new FakeFileDialogService(),
            clipboard ?? new FakeClipboardService(),
            shell ?? new FakeShellService(),
            () => FixedNow,
            association ?? new FakeAssociationService());

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

    private static async Task<MainViewModel> GeneratedModel(string text)
    {
        var model = NewModel(PipelineReturning(text));
        await model.GenerateAsync();
        return model;
    }

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

        // Phase 7 wires the preview commands to real handlers.
        Assert.True(model.PreviewCommand.CanExecute(null));
        Assert.True(model.OpenMarkdownCommand.CanExecute(null));
        Assert.False(model.PreviewVisible);
        Assert.False(model.ShowSourceView);
        Assert.True(model.ShowEmptyState);
        Assert.Equal("Preview MD", model.PreviewLabel);
        Assert.True(model.PreviewHostAvailable);
        Assert.Equal("", model.PreviewHtml);
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

    [Fact]
    public async Task Preview_toggles_the_rendered_document_and_the_source_view()
    {
        var model = await GeneratedModel("# Title\n\ntext\n");

        Assert.False(model.PreviewVisible);
        Assert.True(model.ShowSourceView);
        Assert.Equal("Preview MD", model.PreviewLabel);

        model.PreviewCommand.Execute(null);

        Assert.True(model.PreviewVisible);
        Assert.False(model.ShowSourceView);
        Assert.Equal("Show source", model.PreviewLabel);
        Assert.Equal("Markdown preview", model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
        Assert.Contains("<h1>Title</h1>", model.PreviewHtml, StringComparison.Ordinal);

        model.PreviewCommand.Execute(null);

        Assert.False(model.PreviewVisible);
        Assert.True(model.ShowSourceView);
        Assert.Equal("Preview MD", model.PreviewLabel);
        Assert.Equal("source view", model.StatusMessage);
    }

    [Fact]
    public async Task Preview_refuses_json_output_with_the_reference_wording()
    {
        var model = NewModel(PipelineReturning("{\"a\":1}"));
        model.Mode = "json";
        await model.GenerateAsync();

        model.PreviewCommand.Execute(null);

        Assert.False(model.PreviewVisible);
        Assert.Equal(
            "JSON output is not Markdown - choose memory, full, or transcript",
            model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    [Fact]
    public void Preview_without_output_opens_the_file_dialog()
    {
        var dialogs = new FakeFileDialogService { OpenResult = null };
        var model = NewModel(dialogs: dialogs);

        model.PreviewCommand.Execute(null);

        // The reference falls through to openMarkdownFile, whose cancel message wins.
        Assert.Equal("no file chosen - nothing changed", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
        Assert.False(model.PreviewVisible);
    }

    [Fact]
    public async Task Opening_a_markdown_file_previews_it_and_names_it_in_the_caption()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "README.md");
        File.WriteAllText(path, "# Opened\n\nbody\n");

        var dialogs = new FakeFileDialogService { OpenResult = path };
        var model = NewModel(dialogs: dialogs);

        model.OpenMarkdownCommand.Execute(null);

        Assert.True(model.PreviewVisible);
        Assert.True(model.PreviewIsFile);
        Assert.Equal("README.md", model.PreviewFileName);
        Assert.Equal("README.md", model.PreviewCaption);
        Assert.Equal("reading README.md", model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
        Assert.Contains("<h1>Opened</h1>", model.PreviewHtml, StringComparison.Ordinal);
        Assert.Equal(DialogFilters.MarkdownPreview, dialogs.LastFilter);

        // Closing a file preview returns the caption to the stats.
        model.PreviewCommand.Execute(null);
        Assert.Equal("preview closed", model.StatusMessage);
        Assert.Equal(model.Stats, model.PreviewCaption);
    }

    [Fact]
    public async Task Opening_a_bad_file_reports_the_guard_message_as_an_error()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "notes.txt");
        File.WriteAllText(path, "text");

        var model = NewModel(dialogs: new FakeFileDialogService { OpenResult = path });

        model.OpenMarkdownCommand.Execute(null);

        Assert.Equal(
            "cannot open Markdown file: Choose a Markdown file (.md or .markdown).",
            model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
        Assert.False(model.PreviewVisible);
    }

    [Fact]
    public void A_dropped_path_goes_through_the_same_guard()
    {
        using var temp = new TempDirectory();
        var good = Path.Combine(temp.Path, "dropped.markdown");
        File.WriteAllText(good, "## Dropped\n");
        var model = NewModel();

        model.OpenMarkdownPath(good);

        Assert.True(model.PreviewVisible);
        Assert.Equal("dropped.markdown", model.PreviewFileName);
        Assert.Contains("<h2>Dropped</h2>", model.PreviewHtml, StringComparison.Ordinal);

        model.OpenMarkdownPath(Path.Combine(temp.Path, "missing.md"));
        Assert.Equal(
            "cannot open Markdown file: No such file: " + Path.Combine(temp.Path, "missing.md"),
            model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
    }

    [Fact]
    public async Task A_missing_preview_host_explains_how_to_install_it()
    {
        var model = await GeneratedModel("# Title\n");

        model.SetPreviewHostAvailable(false);
        Assert.False(model.PreviewHostAvailable);

        model.PreviewCommand.Execute(null);

        Assert.Equal(MainViewModel.PreviewUnavailableMessage, model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
        Assert.False(model.PreviewVisible);

        model.OpenMarkdownCommand.Execute(null);
        Assert.Equal(MainViewModel.PreviewUnavailableMessage, model.StatusMessage);
        Assert.Contains("developer.microsoft.com", model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void External_links_are_validated_before_the_shell_sees_them()
    {
        var shell = new FakeShellService();
        var model = NewModel(shell: shell);

        model.OpenExternalLink("https://example.test/a");
        Assert.Equal("https://example.test/a", shell.LastExternal);

        shell.OpenExternalError = new InvalidOperationException("no handler");
        model.OpenExternalLink("https://example.test/b");
        Assert.Equal(
            "could not open link: no handler", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);

        shell.OpenExternalError = null;
        var previous = shell.LastExternal;
        model.OpenExternalLink("javascript:alert(1)");
        model.OpenExternalLink("file:///c:/secret");
        model.OpenExternalLink("#anchor");
        model.OpenExternalLink("");
        Assert.Equal(previous, shell.LastExternal);
    }

    [Fact]
    public async Task A_new_generate_closes_the_preview_and_rebuilds_it_on_demand()
    {
        var text = "# First\n";
        var pipeline = new FakeBuildPipeline
        {
            Handler = (_, _) => Task.FromResult(new BuildResult
            {
                Text = text,
                Stats = "stats",
                SessionCount = 1,
            }),
        };
        var model = NewModel(pipeline);
        await model.GenerateAsync();
        model.PreviewCommand.Execute(null);
        Assert.True(model.PreviewVisible);
        Assert.Contains("<h1>First</h1>", model.PreviewHtml, StringComparison.Ordinal);

        // gui.py:757-758: a fresh build drops back to the source view.
        text = "# Second\n";
        await model.GenerateAsync();
        Assert.False(model.PreviewVisible);
        Assert.True(model.ShowSourceView);

        model.PreviewCommand.Execute(null);

        Assert.Contains("<h1>Second</h1>", model.PreviewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>First</h1>", model.PreviewHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_reports_that_this_copy_owns_the_association()
    {
        var association = new FakeAssociationService { State = AssociationState.Registered };
        var model = NewModel(association: association);

        Assert.True(model.AssociationRegistered);
        Assert.Contains(
            "Windows still asks you to confirm", model.AssociationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_reports_another_copy_without_claiming_it()
    {
        var association = new FakeAssociationService
        {
            State = AssociationState.RegisteredForAnotherCopy,
        };
        var model = NewModel(association: association);

        Assert.False(model.AssociationRegistered);
        Assert.Contains("another copy", model.AssociationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_reports_an_unregistered_app()
    {
        var model = NewModel(association: new FakeAssociationService());

        Assert.False(model.AssociationRegistered);
        Assert.StartsWith("not registered", model.AssociationStatus);
    }

    [Fact]
    public void Setting_the_default_registers_and_opens_the_windows_confirmation()
    {
        var association = new FakeAssociationService();
        var model = NewModel(association: association);

        model.RegisterAssociationCommand.Execute(null);

        Assert.Equal(1, association.RegisterCalls);
        Assert.Equal(1, association.ConfirmationCalls);
        Assert.True(model.AssociationRegistered);
        Assert.Equal("registered - Windows asks you to confirm the default app", model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
    }

    [Fact]
    public void A_refused_registration_is_reported_and_the_app_stays_usable()
    {
        var association = new FakeAssociationService
        {
            RegisterError = new UnauthorizedAccessException("access denied"),
        };
        var model = NewModel(association: association);

        model.RegisterAssociationCommand.Execute(null);

        Assert.False(model.AssociationRegistered);
        Assert.Equal(0, association.ConfirmationCalls);
        Assert.Equal(
            "cannot register: access denied. Windows may need you to choose the app manually.",
            model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
        Assert.True(model.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public void A_failing_confirmation_keeps_the_registration_and_warns()
    {
        var association = new FakeAssociationService
        {
            ConfirmationError = new InvalidOperationException("no picker"),
        };
        var model = NewModel(association: association);

        model.RegisterAssociationCommand.Execute(null);

        Assert.True(model.AssociationRegistered);
        Assert.Contains("open Windows Default Apps", model.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    [Fact]
    public void Removing_the_association_reports_it()
    {
        var association = new FakeAssociationService { State = AssociationState.Registered };
        var model = NewModel(association: association);

        model.RemoveAssociationCommand.Execute(null);

        Assert.Equal(1, association.UnregisterCalls);
        Assert.False(model.AssociationRegistered);
        Assert.Equal("association removed", model.StatusMessage);
        Assert.Equal(StatusKind.Ok, model.StatusKind);
    }

    [Fact]
    public void A_refused_removal_is_reported()
    {
        var association = new FakeAssociationService
        {
            State = AssociationState.Registered,
            UnregisterError = new UnauthorizedAccessException("access denied"),
        };
        var model = NewModel(association: association);

        model.RemoveAssociationCommand.Execute(null);

        Assert.Equal("cannot remove the association: access denied", model.StatusMessage);
        Assert.Equal(StatusKind.Error, model.StatusKind);
    }

    [Fact]
    public void A_file_handed_over_by_explorer_opens_in_the_preview()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "README.md");
        File.WriteAllText(path, "# Hello\n");
        var model = NewModel();

        model.OpenFromShell(path);

        Assert.True(model.PreviewVisible);
        Assert.True(model.PreviewIsFile);
        Assert.Equal("README.md", model.PreviewFileName);
        Assert.Contains("<h1>Hello</h1>", model.PreviewHtml, StringComparison.Ordinal);
        Assert.Equal("reading README.md", model.StatusMessage);
    }

    [Fact]
    public async Task A_file_sent_during_a_run_opens_after_the_run_finishes()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "queued.md");
        File.WriteAllText(path, "# Queued\n");

        var gate = new TaskCompletionSource<BuildResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var model = NewModel(new FakeBuildPipeline { Handler = (_, _) => gate.Task });

        var run = model.GenerateAsync();
        Assert.True(model.IsBusy);

        model.OpenFromShell(path);

        Assert.False(model.PreviewVisible);
        Assert.Equal("queued queued.md until the current run finishes", model.StatusMessage);

        gate.SetResult(new BuildResult { Text = "# Built\n", Stats = "stats", SessionCount = 1 });
        await run;

        Assert.True(model.PreviewVisible);
        Assert.True(model.PreviewIsFile);
        Assert.Contains("<h1>Queued</h1>", model.PreviewHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_command_line_argument_is_reported_as_a_warning()
    {
        var model = NewModel();

        model.ReportStartupMessage("cannot open \"x.md\": no such file");

        Assert.Equal("cannot open \"x.md\": no such file", model.StatusMessage);
        Assert.Equal(StatusKind.Warn, model.StatusKind);
    }

    private static SessionRow Row(string file, string when) => new()
    {
        File = file,
        When = when,
        Folder = "~/project",
        Turns = 4,
    };
}
