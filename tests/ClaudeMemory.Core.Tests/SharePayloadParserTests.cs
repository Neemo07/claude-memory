using System.Globalization;
using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Rendering;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Share payload parsing, ported from claude_memory.py:834-1017. The fixture is the frozen
/// branching conversation; the golden is the reference's <c>--share-file --only-share</c> output.
/// </summary>
public sealed class SharePayloadParserTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:57Z", CultureInfo.InvariantCulture);

    private static string Fixture => Path.Combine(TestPaths.Fixtures, "share-branching.json");

    [Fact]
    public void Share_golden_matches_the_reference()
    {
        var session = SharePayloadParser.LoadFile(Fixture);
        var actual = SummaryBuilder.Build(new[] { session }, MemoryPipeline.NewOptions(), Now);

        var expected = Golden.Normalise(File.ReadAllText(Path.Combine(TestPaths.Golden, "share-memory.md")));
        var normalised = Golden.Normalise(actual);
        Assert.True(expected == normalised, Golden.Diff("share-memory.md", expected, normalised));
    }

    [Fact]
    public void Imports_only_the_active_branch_and_counts_the_skips()
    {
        var session = SharePayloadParser.LoadFile(Fixture);

        Assert.Equal("share-branch-0001", session.Sid);
        Assert.Equal(Fixture, session.Path);
        Assert.Equal("share-share-branch-0001.json", Path.GetFileName(
            SharePayloadParser.Parse(Json(File.ReadAllText(Fixture))).Path));
        Assert.Equal("shared-link", session.Branch);
        Assert.Equal("claude.ai", session.Model);
        Assert.Equal(4, session.Turns.Count);
        Assert.Equal(1, session.Skipped);
        Assert.DoesNotContain(session.Turns, turn => turn.Text.Contains("ABANDONED", StringComparison.Ordinal));
        Assert.Equal(2, session.Prompts().Count);
    }

    [Fact]
    public void Maps_web_tool_names_and_briefs_their_input()
    {
        var session = SharePayloadParser.LoadFile(Fixture);
        var reply = session.Turns.Single(turn => turn.Role == TurnRoles.Assistant && turn.Tools.Count > 0);

        var tool = Assert.Single(reply.Tools);
        Assert.Equal("Bash", tool.Name);
        Assert.Equal("command=dotnet test", tool.Brief);
    }

    [Fact]
    public void Hides_hidden_blocks_and_thinking_unless_requested()
    {
        var defaultSession = SharePayloadParser.LoadFile(Fixture);
        var defaultText = defaultSession.Turns[1].Text;
        Assert.DoesNotContain("This block is hidden.", defaultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Thinking blocks stay excluded", defaultText, StringComparison.Ordinal);

        var withThinking = SharePayloadParser.LoadFile(Fixture, includeThinking: true);
        Assert.Contains(
            "Thinking blocks stay excluded by default.",
            withThinking.Turns[1].Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("This block is hidden.", withThinking.Turns[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_tool_result_errors_and_compaction_summaries()
    {
        var session = SharePayloadParser.LoadFile(Fixture);

        Assert.Contains("Error: preview crashed with a null reference", session.ErrorLines());
        Assert.Equal(2, session.Summaries.Count);
        Assert.Equal("[shared] Branching share fixture", session.Summaries[0]);
        Assert.Equal("The active branch settled on a read-only preview.", session.Summaries[1]);
    }

    [Fact]
    public void Falls_back_to_a_flat_text_field()
    {
        var session = SharePayloadParser.Parse(Json(
            """{"chat_messages":[{"uuid":"a","sender":"human","text":"flat prompt"}]}"""));

        var turn = Assert.Single(session.Turns);
        Assert.Equal("flat prompt", turn.Text);
        Assert.Equal(TurnRoles.User, turn.Role);
    }

    [Fact]
    public void Uses_block_timestamps_when_the_message_has_none()
    {
        var session = SharePayloadParser.Parse(Json(
            """
            {"chat_messages":[{"uuid":"a","sender":"human","content":[
              {"type":"text","text":"hi","stop_timestamp":"2026-10-04T10:00:00Z"}]}]}
            """));

        var turn = Assert.Single(session.Turns);
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T10:00:00Z", CultureInfo.InvariantCulture), turn.Timestamp);
        Assert.Equal(turn.Timestamp, session.Start);
        Assert.Equal(turn.Timestamp, session.End);
    }

    [Fact]
    public void Rejects_a_saved_html_shell()
    {
        var error = Assert.Throws<ShareException>(
            () => SharePayloadParser.Parse(Json("""{"html":"<html></html>"}""")));

        Assert.Contains("saved share *page*", error.Message, StringComparison.Ordinal);
        Assert.Contains("--share-file", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"chat_messages":[]}""")]
    [InlineData("""{"chat_messages":null}""")]
    public void Rejects_a_payload_without_chat_messages(string payload)
    {
        var error = Assert.Throws<ShareException>(() => SharePayloadParser.Parse(Json(payload)));

        Assert.Contains("no chat_messages in the payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_non_object_payload()
    {
        var error = Assert.Throws<ShareException>(() => SharePayloadParser.Parse(Json("[1, 2]")));

        Assert.Equal("share payload is not a JSON object", error.Message);
    }

    [Fact]
    public void Loads_a_raw_message_array_as_a_share_payload()
    {
        using var folder = new TempFolder();
        var path = folder.File("array-export.json");
        File.WriteAllText(
            path,
            """[{"uuid":"a","sender":"human","content":[{"type":"text","text":"hi"}]}]""");

        var session = SharePayloadParser.LoadFile(path);

        Assert.Equal("array-export", session.Sid);
        Assert.Equal(path, session.Path);
        Assert.Equal("[shared] array-export", Assert.Single(session.Summaries));
        Assert.Equal("hi", Assert.Single(session.Turns).Text);
    }

    [Fact]
    public void Reports_unreadable_missing_and_non_json_files()
    {
        using var folder = new TempFolder();

        var missing = Assert.Throws<ShareException>(
            () => SharePayloadParser.LoadFile(folder.File("absent.json")));
        Assert.Contains("cannot read", missing.Message, StringComparison.Ordinal);

        var html = folder.File("page.html");
        File.WriteAllText(html, "<!doctype html><html></html>");
        var notJson = Assert.Throws<ShareException>(() => SharePayloadParser.LoadFile(html));
        Assert.Contains("does not look like JSON", notJson.Message, StringComparison.Ordinal);

        var broken = folder.File("broken.json");
        File.WriteAllText(broken, "{ nope");
        var invalid = Assert.Throws<ShareException>(() => SharePayloadParser.LoadFile(broken));
        Assert.Contains("is not valid JSON", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("""{"stdout":"out"}""", "out")]
    [InlineData("""{"message":"","content":"from content"}""", "from content")]
    [InlineData("""[{"text":"a"},"b"]""", "a\nb")]
    [InlineData("0", "")]
    public void Result_text_accepts_every_payload_shape(string body, string expected)
    {
        Assert.Equal(expected, SharePayloadParser.ResultText(Json(body)));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}