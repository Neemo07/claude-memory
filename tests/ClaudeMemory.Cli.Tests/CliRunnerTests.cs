using System.Globalization;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// End-to-end parity for <c>claude_memory.py main()</c> (claude_memory.py:1870-2027): the mode
/// outputs against the frozen goldens, the stderr lines, and the exit codes 0/1/2/3/130.
/// </summary>
public sealed class CliRunnerTests
{
    private static string[] RootArgs(params string[] extra)
    {
        var args = new List<string> { "--root", TestPaths.FixtureRoot };
        args.AddRange(extra);
        return args.ToArray();
    }

    private static void AssertGolden(string goldenName, string actual, bool json = false)
    {
        var expected = json ? Golden.NormaliseJson(Golden.ReadGolden(goldenName)) : Golden.Normalise(Golden.ReadGolden(goldenName));
        var normalised = json ? Golden.NormaliseJson(actual) : Golden.Normalise(actual);

        Assert.True(expected == normalised, Golden.Diff(goldenName, expected, normalised));
    }

    [Fact]
    public void Memory_plain_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs(), out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        AssertGolden("memory-plain.md", stdout);
    }

    [Fact]
    public void Claude_md_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--format", "claude-md"), out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        AssertGolden("memory-claude-md.md", stdout);
    }

    [Fact]
    public void Ascii_fold_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--ascii"), out var stdout, out _);

        Assert.Equal(0, code);
        AssertGolden("memory-ascii.md", stdout);
    }

    [Fact]
    public void Bilingual_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--lang", "both"), out var stdout, out _);

        Assert.Equal(0, code);
        AssertGolden("memory-lang-both.md", stdout);
    }

    [Fact]
    public void Full_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--mode", "full"), out var stdout, out _);

        Assert.Equal(0, code);
        AssertGolden("full-plain.md", stdout);
    }

    [Fact]
    public void Transcript_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--mode", "transcript"), out var stdout, out _);

        Assert.Equal(0, code);
        AssertGolden("transcript.md", stdout);
    }

    [Fact]
    public void Json_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--mode", "json"), out var stdout, out _);

        Assert.Equal(0, code);
        AssertGolden("json.json", stdout, json: true);
    }

    [Fact]
    public void List_matches_the_golden()
    {
        var code = CliHarness.Run(RootArgs("--list"), out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        AssertGolden("list.md", stdout);
    }

    [Fact]
    public void Share_file_import_matches_the_golden()
    {
        var code = CliHarness.Run(
            new[] { "--share-file", TestPaths.ShareFixture, "--only-share" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Contains("imported share payload from ", stderr, StringComparison.Ordinal);
        AssertGolden("share-memory.md", stdout);
    }

    [Fact]
    public void A_missing_transcript_dir_exits_2_with_the_reference_hint()
    {
        using var empty = new TempFolder();

        var code = CliHarness.Run(new[] { "--root", empty.Path }, out var stdout, out var stderr);

        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Equal(
            "[claude-memory] no transcript dir at " + Path.Combine(empty.Path, "projects") + "\n"
            + "[claude-memory] run a Claude Code session in a project, pass --projects-dir, "
            + "or import a share link with --share.\n",
            stderr);
    }

    [Fact]
    public void No_matching_sessions_exits_1()
    {
        using var empty = new TempFolder();

        var code = CliHarness.Run(
            new[] { "--root", empty.Path, "--projects-dir", empty.Path }, out var stdout, out var stderr);

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Equal(
            "[claude-memory] no sessions matched (searched 0 of 0).\n"
            + "[claude-memory] try widening --since / --cwd, or drop --sessions.\n",
            stderr);
    }

    [Fact]
    public void An_empty_list_exits_0_with_the_reference_message()
    {
        using var empty = new TempFolder();

        var code = CliHarness.Run(
            new[] { "--root", empty.Path, "--projects-dir", empty.Path, "--list" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal(
            "No Claude Code transcripts found.\n"
            + "Expected .jsonl files under ~/.claude/projects/<slug>/.\n"
            + "Start a Claude Code session in a project directory first.\n",
            stdout);
    }

    [Fact]
    public void An_invalid_share_link_exits_3()
    {
        var code = CliHarness.Run(new[] { "--share", "nope" }, out var stdout, out var stderr);

        Assert.Equal(3, code);
        Assert.Equal("", stdout);
        Assert.Equal(
            "[claude-memory] not a claude.ai share link: 'nope'\n"
            + "expected https://claude.ai/share/<uuid>\n",
            stderr);
    }

    [Fact]
    public void A_cancelled_run_exits_130_with_the_interrupt_line()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var code = CliHarness.Run(
            new[] { "--share", "https://claude.ai/share/00000000-0000-0000-0000-000000000000" },
            out var stdout,
            out var stderr,
            cancellationToken: cancellation.Token);

        Assert.Equal(130, code);
        Assert.Equal("", stdout);
        Assert.Equal("\n[claude-memory] interrupted\n", stderr);
    }

    [Fact]
    public void Cwd_filter_selects_a_match_and_rejects_a_miss()
    {
        var hit = CliHarness.Run(RootArgs("--cwd", "demo"), out var stdout, out _);
        Assert.Equal(0, hit);
        Assert.Contains("demo", stdout, StringComparison.OrdinalIgnoreCase);

        var miss = CliHarness.Run(RootArgs("--cwd", "zzz-no-such-project"), out var missStdout, out var missStderr);
        Assert.Equal(1, miss);
        Assert.Equal("", missStdout);
        Assert.Contains("no sessions matched", missStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Quiet_suppresses_the_stderr_summary()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        var code = CliHarness.Run(
            RootArgs("--out", outPath, "--quiet"), out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.True(File.Exists(outPath));
        AssertGolden("memory-plain.md", File.ReadAllText(outPath));
    }

    [Fact]
    public void Out_with_print_writes_the_file_and_stdout_and_reports_the_summary()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        var code = CliHarness.Run(RootArgs("--out", outPath, "--print"), out var stdout, out var stderr);

        Assert.Equal(0, code);
        AssertGolden("memory-plain.md", stdout);
        Assert.Contains("[claude-memory] wrote " + outPath + " (", stderr, StringComparison.Ordinal);
        Assert.Contains("session(s),", stderr, StringComparison.Ordinal);
        Assert.Contains("paste-ready block: re-run with --format claude-md", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_md_out_reports_the_append_hint_not_the_paste_ready_hint()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        var code = CliHarness.Run(
            RootArgs("--out", outPath, "--format", "claude-md"), out var _, out var stderr);

        Assert.Equal(0, code);
        Assert.Contains("file already carries CLAUDE.md paste markers;", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("paste-ready block:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_mode_memory_archives_the_previous_content_on_the_second_run()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        var first = CliHarness.Run(RootArgs("--out", outPath, "--history", "2"), out _, out _);
        var second = CliHarness.Run(RootArgs("--out", outPath, "--history", "2"), out _, out _);

        Assert.Equal(0, first);
        Assert.Equal(0, second);

        var history = Path.Combine(folder.Path, "MEMORY.history");
        var files = Directory.GetFiles(history, "*.md");
        Assert.Single(files);

        // The fixed clock stamps 2026-11-04T15:11:55Z.
        Assert.Equal("20261104-151155-1.md", Path.GetFileName(files[0]));
        AssertGolden("memory-plain.md", File.ReadAllText(files[0]));
    }

    [Fact]
    public void History_retention_prunes_to_the_requested_count()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        for (var run = 0; run < 4; run++)
        {
            var code = CliHarness.Run(RootArgs("--out", outPath, "--history", "2"), out _, out _);
            Assert.Equal(0, code);
        }

        var files = Directory.GetFiles(Path.Combine(folder.Path, "MEMORY.history"), "*.md");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Transcript_and_json_modes_never_archive_history()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        Assert.Equal(0, CliHarness.Run(RootArgs("--out", outPath, "--mode", "transcript"), out _, out _));
        Assert.Equal(0, CliHarness.Run(RootArgs("--out", outPath, "--mode", "transcript"), out _, out _));

        Assert.False(Directory.Exists(Path.Combine(folder.Path, "MEMORY.history")));
    }

    [Fact]
    public void Fast_skips_transcripts_older_than_the_cutoff_by_mtime()
    {
        using var folder = new TempFolder();
        var projects = Path.Combine(folder.Path, "projects", "p");
        Directory.CreateDirectory(projects);

        var fresh = Path.Combine(projects, "synthetic.jsonl");
        var stale = Path.Combine(projects, "arabic.jsonl");
        File.Copy(Path.Combine(TestPaths.FixtureRoot, "projects", "-C-work-demo", "synthetic.jsonl"), fresh);
        File.Copy(Path.Combine(TestPaths.FixtureRoot, "projects", "-C-work-arabic", "arabic.jsonl"), stale);

        File.SetLastWriteTimeUtc(fresh, CliHarness.Now.UtcDateTime);
        File.SetLastWriteTimeUtc(stale, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var code = CliHarness.Run(
            new[] { "--root", folder.Path, "--fast", "--since", "7d", "--list" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Contains(
            "[claude-memory] --fast: skipped 1 transcript(s) older than 2026-10-28 "
            + "by file mtime (approximate)\n",
            stderr,
            StringComparison.Ordinal);
        Assert.Contains("synthetic.jsonl", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("arabic.jsonl", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Fast_without_since_skips_nothing()
    {
        using var folder = new TempFolder();
        var projects = Path.Combine(folder.Path, "projects", "p");
        Directory.CreateDirectory(projects);

        var stale = Path.Combine(projects, "arabic.jsonl");
        File.Copy(Path.Combine(TestPaths.FixtureRoot, "projects", "-C-work-arabic", "arabic.jsonl"), stale);
        File.SetLastWriteTimeUtc(stale, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var code = CliHarness.Run(
            new[] { "--root", folder.Path, "--fast", "--list" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("arabic.jsonl", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_chars_below_the_floor_warns_on_stderr_for_memory_only()
    {
        var memory = CliHarness.Run(RootArgs("--max-chars", "5"), out var memoryOut, out var memoryErr);
        Assert.Equal(0, memory);
        Assert.Contains(
            "[claude-memory] --max-chars 5 raised to floor " + Budget.Floor + "\n",
            memoryErr,
            StringComparison.Ordinal);
        Assert.NotEqual("", memoryOut);

        var json = CliHarness.Run(RootArgs("--mode", "json", "--max-chars", "5"), out _, out var jsonErr);
        Assert.Equal(0, json);
        Assert.DoesNotContain("raised to floor", jsonErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Print_adds_a_newline_only_when_the_body_lacks_one()
    {
        var code = CliHarness.Run(RootArgs(), out var stdout, out _);

        Assert.Equal(0, code);
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        Assert.False(stdout.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_and_version_are_handled_without_touching_the_transcripts()
    {
        using var empty = new TempFolder();

        // An empty root would exit 2 for a normal run; help/version short-circuit before that.
        Assert.Equal(0, CliHarness.Run(new[] { "--root", empty.Path, "--help" }, out var help, out _));
        Assert.StartsWith("usage: claude_memory.py", help, StringComparison.Ordinal);

        Assert.Equal(0, CliHarness.Run(new[] { "--root", empty.Path, "--version" }, out var version, out _));
        Assert.Equal("claude_memory.py 1.1.0\n", version);
    }

    [Fact]
    public void The_exit_code_table_is_wired_to_the_documented_numbers()
    {
        Assert.Equal(0, CliHarness.Run(RootArgs(), out _, out _));
        Assert.Equal(2, CliHarness.Run(new[] { "--bogus" }, out _, out _));
        Assert.Equal(3, CliHarness.Run(new[] { "--share", "nope" }, out _, out _));
    }

    [Fact]
    public void Wrote_line_counts_code_points_and_lines()
    {
        using var folder = new TempFolder();
        var outPath = folder.File("MEMORY.md");

        CliHarness.Run(RootArgs("--out", outPath), out _, out var stderr);

        var text = File.ReadAllText(outPath);
        var expected = "[claude-memory] wrote " + outPath + " (" + RuneCount(text) + " chars, "
            + "6 session(s), " + TextUtil.SplitLines(text).Count + " lines)\n";

        Assert.StartsWith(expected, stderr, StringComparison.Ordinal);
    }

    private static int RuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }
}
