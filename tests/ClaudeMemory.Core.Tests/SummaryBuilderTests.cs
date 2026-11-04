using System.Globalization;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Behaviour that the golden corpus cannot isolate: the prompt cap, recency dedupe, the trim
/// note and the language auto-detect. Vectors follow claude_memory.py 1.1.0.
/// </summary>
public sealed class SummaryBuilderTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Prompt_cap_drops_the_oldest_prompts()
    {
        var session = NewSession("C:/p");
        session.Turns.Add(NewTurn(TurnRoles.User, "first"));
        session.Turns.Add(NewTurn(TurnRoles.User, "second"));
        session.Turns.Add(NewTurn(TurnRoles.User, "third"));

        var text = SummaryBuilder.Build(
            new[] { session },
            new MemoryOptions { MaxPromptsPerSession = 2, MaxChars = 20000 },
            Now);

        Assert.Contains("(1 earlier prompt(s) omitted; newest 2 shown)", text, StringComparison.Ordinal);
        Assert.Contains("> second", text, StringComparison.Ordinal);
        Assert.Contains("> third", text, StringComparison.Ordinal);
        Assert.DoesNotContain("> first", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_claims_are_rendered_once_from_the_oldest_turn()
    {
        // The shared `seen` set dedupes inside a section before dedupe_recent runs, so the
        // earliest spelling wins when two lines share a normalised key (claude_memory.py:1190-1200).
        var session = NewSession("C:/p");
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "We decided to use the old path."));
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "We decided to use the old path!"));

        var text = SummaryBuilder.Build(new[] { session }, new MemoryOptions { MaxChars = 20000 }, Now);
        var decisions = Section(text, "## Decisions taken");

        Assert.Contains("We decided to use the old path.", decisions, StringComparison.Ordinal);
        Assert.DoesNotContain("We decided to use the old path!", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void Claim_limit_keeps_the_newest_distinct_decisions()
    {
        var session = NewSession("C:/p");
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "We decided to use the alpha path."));
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "We decided to use the beta path."));
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "We decided to use the gamma path."));

        var text = SummaryBuilder.Build(
            new[] { session },
            new MemoryOptions { MaxChars = 20000, MaxItems = 2 },
            Now);
        var decisions = Section(text, "## Decisions taken");

        Assert.DoesNotContain("alpha", decisions, StringComparison.Ordinal);
        Assert.Contains("beta", decisions, StringComparison.Ordinal);
        Assert.Contains("gamma", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_note_reports_the_requested_max_chars()
    {
        var session = NewSession("C:/p");
        session.Turns.Add(NewTurn(
            TurnRoles.Assistant,
            "We decided to " + string.Join(" ", Enumerable.Repeat("alpha", 80)) + "."));

        var text = SummaryBuilder.Build(new[] { session }, new MemoryOptions { MaxChars = 400 }, Now);

        Assert.Contains("_Trimmed ", text, StringComparison.Ordinal);
        Assert.Contains("--max-chars 400", text, StringComparison.Ordinal);
        Assert.EndsWith("full detail._\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Arabic_content_switches_auto_headers_to_bilingual()
    {
        var session = NewSession("C:/p");
        session.Turns.Add(NewTurn(TurnRoles.User, "\u064a\u062c\u0628 \u0623\u0646 \u064a\u0628\u0642\u0649 \u0627\u0644\u062a\u0637\u0628\u064a\u0642 \u062f\u0648\u0646 \u0627\u062a\u0635\u0627\u0644."));
        session.Turns.Add(NewTurn(TurnRoles.Assistant, "\u0627\u0644\u062e\u0637\u0648\u0627\u062a \u0627\u0644\u062a\u0627\u0644\u064a\u0629: \u0627\u062e\u062a\u0628\u0631 \u0627\u0644\u0648\u0627\u062c\u0647\u0629."));

        var text = SummaryBuilder.Build(new[] { session }, new MemoryOptions { MaxChars = 20000 }, Now);

        Assert.Contains("# Project memory \u2014 rolling | ", text, StringComparison.Ordinal);
        Assert.Contains("## Sessions | ", text, StringComparison.Ordinal);
    }

    private static Session NewSession(string cwd) => new("C:/p/x.jsonl") { Cwd = cwd };

    private static Turn NewTurn(string role, string text) => new() { Role = role, Text = text };

    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing heading: " + heading);

        var next = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }
}
