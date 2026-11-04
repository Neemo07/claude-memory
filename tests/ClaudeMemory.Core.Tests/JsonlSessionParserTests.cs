using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Expectations come from running the Python reference over the frozen fixture corpus
/// (<c>tests/fixtures/fixture_root</c>).
/// </summary>
public sealed class JsonlSessionParserTests
{
    private const string TestProject = "-C-testproj";

    private static string Transcript(string project, string name)
        => Path.Combine(TestPaths.FixtureRoot, "projects", project, name);

    private static Session Parse(string project, string name, MemoryOptions? opts = null)
        => JsonlSessionParser.ParseSession(Transcript(project, name), opts ?? new MemoryOptions());

    [Fact]
    public void ParseSession_ReadsHeaderFieldsAndCounts()
    {
        var session = Parse(TestProject, "11111111-2222-3333-4444-555555555555.jsonl");

        Assert.Equal("11111111-2222-3333-4444-555555555555", session.Sid);
        Assert.Equal("C:/testproj", session.Cwd);
        Assert.Equal("main", session.Branch);
        Assert.Equal("2.0.14", session.Version);
        Assert.Equal("claude-sonnet-4-5", session.Model);
        Assert.Equal(7, session.Turns.Count);
        Assert.Equal(5, session.Skipped);
        Assert.Single(session.Summaries);
        Assert.Equal(2, session.Prompts().Count);
        Assert.Equal(5, session.ToolCalls().Count);
        Assert.False(session.TruncatedFile);
    }

    [Fact]
    public void ParseSession_TracksTheTimestampSpan()
    {
        var session = Parse(TestProject, "11111111-2222-3333-4444-555555555555.jsonl");

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), session.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 14, 0, TimeSpan.Zero), session.End);
        Assert.Equal(840.0, session.Duration);
    }

    [Fact]
    public void ParseSession_SummarisesToolCallsAndErrors()
    {
        var session = Parse(TestProject, "11111111-2222-3333-4444-555555555555.jsonl");
        var calls = session.ToolCalls();

        Assert.Equal(new ToolCall("Read", @"file_path=C:\testproj\src\net\packet.cpp"), calls[0].Call);
        Assert.Equal(new ToolCall("Read", @"file_path=C:\testproj\src\net\packet.h"), calls[1].Call);
        Assert.Equal(new ToolCall("Edit", @"file_path=C:\testproj\src\net\packet.cpp"), calls[2].Call);
        Assert.Equal(new ToolCall("Bash", "command=python build_full.py --workers 4"), calls[3].Call);
        Assert.Equal(new ToolCall("Glob", "pattern=**/build*.py"), calls[4].Call);
        Assert.All(calls, call => Assert.False(call.Sidechain));

        Assert.Equal(
            new[] { "error: no such file or directory: 'build_full.py' Build failed after 812 ms" },
            session.ErrorLines());
    }

    [Fact]
    public void ParseSession_KeepsAnErrorOnlyTurnAsAUserTurn()
    {
        var session = Parse(TestProject, "11111111-2222-3333-4444-555555555555.jsonl");
        var errorTurn = session.Turns[5];

        Assert.Equal(TurnRoles.User, errorTurn.Role);
        Assert.Equal("", errorTurn.Text);
        Assert.Empty(errorTurn.Tools);
        Assert.Single(errorTurn.Errors);
    }

    [Fact]
    public void ParseSession_ReadsASecondSessionInTheSameProject()
    {
        var session = Parse(TestProject, "99999999-8888-7777-6666-555555555555.jsonl");

        Assert.Equal(5, session.Turns.Count);
        Assert.Equal(2, session.Skipped);
        Assert.Equal(2, session.Prompts().Count);
        Assert.Equal(2, session.ToolCalls().Count);
        Assert.Empty(session.ErrorLines());
    }

    [Fact]
    public void ParseSession_MarksSidechainTurns()
    {
        var session = Parse("-C-alpha-srv", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl");

        Assert.Equal(3, session.Turns.Count);
        Assert.Equal(0, session.Skipped);
        Assert.False(session.Turns[1].Sidechain);
        Assert.True(session.Turns[2].Sidechain);
        Assert.Equal(new ToolCall("Task", "explore: find hardcoded packet sizes"), session.ToolCalls()[0].Call);
    }

    [Fact]
    public void ParseSession_SkipsSidechainTurnsWhenAsked()
    {
        var session = Parse("-C-work-demo", "synthetic.jsonl", new MemoryOptions { Sidechains = MemoryOptions.SidechainsDrop });

        Assert.Equal(9, session.Turns.Count);
        Assert.Equal(3, session.Skipped);
        Assert.All(session.Turns, turn => Assert.False(turn.Sidechain));
    }

    [Fact]
    public void ParseSession_ExcludesThinkingUnlessRequested()
    {
        var defaultSession = Parse("-C-work-demo", "synthetic.jsonl");
        Assert.All(defaultSession.Turns, turn => Assert.DoesNotContain("Internal reasoning", turn.Text));

        var withThinking = Parse("-C-work-demo", "synthetic.jsonl", new MemoryOptions { IncludeThinking = true });
        Assert.Equal(defaultSession.Turns.Count, withThinking.Turns.Count);
        Assert.StartsWith(
            "Internal reasoning must be excluded by default.\nI will delegate the inspection to a subagent.",
            withThinking.Turns[3].Text);
    }

    [Fact]
    public void ParseSession_SkipsMalformedLinesAndMetaRecords()
    {
        var session = Parse("-C-work-demo", "synthetic.jsonl");

        Assert.Equal(10, session.Turns.Count);
        Assert.Equal(2, session.Skipped);
        Assert.Equal(3, session.Prompts().Count);
        Assert.Equal(2, session.ToolCalls().Count);
        Assert.Single(session.Summaries);
        Assert.Equal(new[] { "Error: CS0103 preview is not defined" }, session.ErrorLines());
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), session.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 12, 0, TimeSpan.Zero), session.End);
        Assert.Equal(720.0, session.Duration);

        // The meta record and the non-message "note" record are skipped, never turned into turns.
        Assert.DoesNotContain(session.Turns, turn => turn.Text.Contains("Meta records must be skipped"));
        Assert.DoesNotContain(session.Turns, turn => turn.Text.Contains("System note that is not a message"));
    }

    [Fact]
    public void ParseSession_ReadsArabicContent()
    {
        var session = Parse("-C-work-arabic", "arabic.jsonl");

        Assert.Equal("arabic-0001", session.Sid);
        Assert.Equal("C:/work/arabic", session.Cwd);
        Assert.Equal(4, session.Turns.Count);
        Assert.Equal(2, session.Prompts().Count);
        Assert.Empty(session.ToolCalls());
        Assert.Equal("يجب أن يبقى التطبيق دون اتصال. أضف معاينة Markdown.", session.Label());
    }

    [Fact]
    public void ParseSession_SurvivesAFileWithMalformedFirstLine()
    {
        var session = Parse(TestProject, "corrupt.jsonl");

        Assert.Equal("corrupt", session.Sid);
        Assert.Single(session.Turns);
        Assert.Equal(0, session.Skipped);
        Assert.Equal(new[] { "orphan summary" }, session.Summaries);
        Assert.Null(session.Start);
        Assert.Null(session.End);
        Assert.Equal("ok", session.Turns[0].Text);
    }

    [Fact]
    public void ParseSession_ReturnsAnEmptySessionForAnEmptyFile()
    {
        var session = Parse(TestProject, "empty.jsonl");

        Assert.Empty(session.Turns);
        Assert.Empty(session.Summaries);
        Assert.Equal(0, session.Skipped);
        Assert.Null(session.Start);
    }

    [Fact]
    public void ParseSession_ReturnsAnEmptySessionForAMissingFile()
    {
        var session = JsonlSessionParser.ParseSession(
            Path.Combine(TestPaths.FixtureRoot, "projects", TestProject, "does-not-exist.jsonl"),
            new MemoryOptions());

        Assert.Empty(session.Turns);
        Assert.False(session.TruncatedFile);
    }

    [Fact]
    public void IterRecords_ReadsTheWholeFileWhenUnderTheCap()
    {
        var (records, truncated) = JsonlSessionParser.IterRecords(
            Transcript(TestProject, "99999999-8888-7777-6666-555555555555.jsonl"),
            1024 * 1024);

        Assert.False(truncated);
        Assert.Equal(8, records.Count);
    }

    [Fact]
    public void IterRecords_ReadsTheTailAndDropsThePartialFirstLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "cm-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");

        try
        {
            // The file is 24 bytes; a 10-byte cap starts inside the second record.
            var (records, truncated) = JsonlSessionParser.IterRecords(path, 10);

            Assert.True(truncated);
            Assert.Single(records);
            Assert.Equal(3, records[0].GetProperty("c").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IterRecords_DropsNonObjectRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), "cm-shape-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "[1, 2]\n42\n{\"ok\":true}\n");

        try
        {
            var (records, truncated) = JsonlSessionParser.IterRecords(path, 1024);

            Assert.False(truncated);
            Assert.Single(records);
            Assert.Equal(JsonValueKind.Object, records[0].ValueKind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}