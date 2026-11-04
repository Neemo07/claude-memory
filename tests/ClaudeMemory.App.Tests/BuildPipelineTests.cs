using System.IO;
using ClaudeMemory.App.Services;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// The desktop pipeline must produce exactly what the headless host produces for the same
/// inputs (IMPLEMENTATION_PLAN.md Phase 6), so these tests compare it with a local replay of
/// the stages in <c>CliRunner.ExecuteAsync</c>.
/// </summary>
public sealed class BuildPipelineTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static BuildPipeline FixturePipeline() => new(
        home: TestPaths.RepoRoot,
        root: TestPaths.FixtureRoot,
        currentDirectory: TestPaths.FixtureRoot,
        now: () => FixedNow,
        defaultOutputDirectory: TestPaths.RepoRoot);

    [Theory]
    [InlineData("memory")]
    [InlineData("full")]
    [InlineData("transcript")]
    [InlineData("json")]
    public async Task Matches_the_headless_pipeline(string mode)
    {
        var result = await FixturePipeline().BuildAsync(new BuildRequest { Mode = mode });

        var options = new MemoryOptions
        {
            Home = TestPaths.RepoRoot,
            Root = TestPaths.FixtureRoot,
            Mode = mode,
            Quiet = true,
        };

        var parsed = SessionDiscovery.Discover(options.Root, null)
            .Select(path => JsonlSessionParser.ParseSession(path, options))
            .Where(session => session.Turns.Count > 0 || session.Summaries.Count > 0)
            .ToList();
        var chosen = SessionOrder.OldestFirst(
            SessionSelector.Select(parsed, options, TestPaths.FixtureRoot));

        var expected = mode switch
        {
            "transcript" => TranscriptBuilder.Build(chosen, options, FixedNow),
            "full" => FullMemoryBuilder.Build(chosen, options, FixedNow),
            "json" => JsonExporter.Build(chosen, FixedNow),
            _ => SummaryBuilder.Build(chosen, options, FixedNow),
        };

        Assert.Equal(expected, result.Text);
        Assert.Equal(chosen.Count, result.SessionCount);
        Assert.StartsWith($"{chosen.Count} session(s) · ", result.Stats);
        Assert.Contains("chars ~", result.Stats);
        Assert.Contains("tokens", result.Stats);
    }

    [Fact]
    public async Task Stats_line_matches_the_reference_shape()
    {
        var result = await FixturePipeline().BuildAsync(new BuildRequest());
        var parts = result.Stats.Split(" · ");

        Assert.Equal(4, parts.Length);
        Assert.EndsWith(" session(s)", parts[0]);
        Assert.EndsWith(" prompt(s)", parts[1]);
        Assert.EndsWith(" tool call(s)", parts[2]);
        Assert.Matches(@"^\d+ chars ~\d+ tokens$", parts[3]);
    }

    [Fact]
    public async Task Rejects_an_unparseable_window_with_the_gui_wording()
    {
        var error = await Assert.ThrowsAsync<BuildException>(
            () => FixturePipeline().BuildAsync(new BuildRequest { Since = "soon" }));

        Assert.Equal(
            "cannot understand the time window 'soon'. Try 30d, 12h, 30m or 2026-10-01.",
            error.Message);
    }

    [Fact]
    public void Options_fall_back_on_junk_and_unknown_choices()
    {
        var request = new BuildRequest
        {
            Mode = "nonsense",
            Format = "x",
            Lang = "xx",
            Sessions = "abc",
            MaxChars = "-5",
            MaxItems = "",
            Cwd = "alpha",
            IncludeThinking = true,
            DropSidechains = true,
        };

        var options = BuildPipeline.BuildOptions(
            request, @"C:\home", @"C:\home\.claude", FixedNow);

        Assert.Equal("memory", options.Mode);
        Assert.Equal("plain", options.Format);
        Assert.Equal("auto", options.Lang);
        Assert.Equal(0, options.Sessions);
        Assert.Equal(-5, options.MaxChars);
        Assert.Equal(14, options.MaxItems);
        Assert.Equal("alpha", options.Cwd);
        Assert.True(options.IncludeThinking);
        Assert.Equal(MemoryOptions.SidechainsDrop, options.Sidechains);
        Assert.True(options.Quiet);
        Assert.Null(options.Since);
        Assert.Equal(@"C:\home\.claude", options.Root);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData(" 7 ", 7)]
    [InlineData("-3", -3)]
    [InlineData("", 9)]
    [InlineData("junk", 9)]
    public void As_int_falls_back_like_the_reference(string value, int expected)
        => Assert.Equal(expected, BuildPipeline.AsInt(value, 9));

    [Fact]
    public async Task Reports_no_sessions_with_the_gui_wording()
    {
        var pipeline = new BuildPipeline(
            home: TestPaths.RepoRoot,
            root: Path.Combine(TestPaths.RepoRoot, "no-such-root"),
            currentDirectory: TestPaths.RepoRoot,
            now: () => FixedNow);

        var error = await Assert.ThrowsAsync<BuildException>(
            () => pipeline.BuildAsync(new BuildRequest()));

        Assert.StartsWith("Nothing to summarise.", error.Message);
        Assert.Contains("untick 'this folder only'", error.Message);
    }

    [Fact]
    public async Task Share_source_requires_a_link_or_a_payload_file()
    {
        var error = await Assert.ThrowsAsync<BuildException>(
            () => FixturePipeline().BuildAsync(
                new BuildRequest { Source = MemorySource.ShareOnly }));

        Assert.Equal(
            "Paste a claude.ai share link (or a saved payload path) first.", error.Message);
    }

    [Fact]
    public async Task Share_source_reports_a_missing_payload_file()
    {
        var missing = Path.Combine(TestPaths.FixtureRoot, "missing-payload.json");

        var error = await Assert.ThrowsAsync<BuildException>(
            () => FixturePipeline().BuildAsync(new BuildRequest
            {
                Source = MemorySource.ShareOnly,
                ShareFile = missing,
            }));

        Assert.Equal("No such file: " + missing, error.Message);
    }

    [Fact]
    public async Task Imports_a_saved_share_payload()
    {
        var payload = Path.Combine(TestPaths.Fixtures, "share-branching.json");
        Assert.True(File.Exists(payload));

        var result = await FixturePipeline().BuildAsync(new BuildRequest
        {
            Source = MemorySource.ShareOnly,
            ShareFile = payload,
        });

        Assert.Equal(1, result.SessionCount);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.StartsWith("1 session(s) · ", result.Stats);
    }

    [Fact]
    public async Task Share_only_ignores_local_sessions()
    {
        var local = await FixturePipeline().BuildAsync(new BuildRequest());
        var payload = Path.Combine(TestPaths.Fixtures, "share-branching.json");
        var shared = await FixturePipeline().BuildAsync(new BuildRequest
        {
            Source = MemorySource.ShareOnly,
            ShareFile = payload,
        });

        Assert.True(local.SessionCount > 1);
        Assert.Equal(1, shared.SessionCount);
    }
}