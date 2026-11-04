using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Vectors for the ported regexes (claude_memory.py:69-137). Each line is taken from the
/// synthetic fixture corpus or from the golden output, so a pattern change shows up here.
/// </summary>
public sealed class HeuristicsTests
{
    [Theory]
    [InlineData("We decided to use Markdig for preview rendering.")]
    [InlineData("Decision: the native function reads the length as a big-endian uint32_t.")]
    [InlineData("I'll use argparse with a dataclass config.")]
    [InlineData("\u0642\u0631\u0631\u0646\u0627 \u0627\u0633\u062a\u062e\u062f\u0627\u0645 Markdown \u0644\u0644\u0639\u0631\u0636.")]
    public void Decision_matches_english_and_arabic_claims(string line)
        => Assert.True(Heuristics.Decision.IsMatch(line), line);

    [Theory]
    [InlineData("The parser must never send transcripts to the network.")]
    [InlineData("It is required to keep the preview read-only.")]
    [InlineData("\u064a\u062c\u0628 \u0623\u0646 \u064a\u0628\u0642\u0649 \u0627\u0644\u062a\u0637\u0628\u064a\u0642 \u062f\u0648\u0646 \u0627\u062a\u0635\u0627\u0644.")]
    public void Fact_matches_english_and_arabic_constraints(string line)
        => Assert.True(Heuristics.Fact.IsMatch(line), line);

    [Theory]
    [InlineData("Error: CS0103 preview is not defined")]
    [InlineData("Subagent found a null reference at src/Preview.cs:42; the root cause is a missing guard.")]
    [InlineData("The preview crashed with a null reference before the guard, and that is fixed now.")]
    public void Problem_matches_english_failures(string line)
        => Assert.True(Heuristics.Problem.IsMatch(line), line);

    [Theory]
    [InlineData("TODO: verify the fix on a 10 MB transcript.")]
    [InlineData("Remaining work:")]
    [InlineData("\u0627\u0644\u062e\u0637\u0648\u0627\u062a \u0627\u0644\u062a\u0627\u0644\u064a\u0629: \u0627\u062e\u062a\u0628\u0631 \u0627\u0644\u0648\u0627\u062c\u0647\u0629.")]
    public void Open_matches_english_and_arabic_open_items(string line)
        => Assert.True(Heuristics.Open.IsMatch(line), line);

    [Theory]
    [InlineData("Convention: always keep the preview read-only.")]
    [InlineData("I prefer argparse with a dataclass config so the tests can build Options directly.")]
    public void Preference_matches_conventions(string line)
        => Assert.True(Heuristics.Preference.IsMatch(line), line);

    [Theory]
    // RX_LOW_VALUE drops acknowledgement-only lines, which is why the "Yes — ..." sentence
    // never reaches the constraints section of the golden.
    [InlineData("Yes \u2014 the reader catches json.JSONDecodeError per line and skips it")]
    [InlineData("ok, sounds good")]
    [InlineData("\u0646\u0639\u0645, \u062a\u0645.")]
    public void LowValue_matches_acknowledgements(string line)
        => Assert.Matches(Heuristics.LowValue, line);

    [Fact]
    public void LowValue_does_not_match_a_bare_acknowledgement()
    {
        // The reference pattern needs one trailing separator character, so the bare prompt
        // "ok" is not a low-value line.
        Assert.DoesNotMatch(Heuristics.LowValue, "ok");
    }

    [Fact]
    public void HeaderLine_matches_short_label_lines_only()
    {
        Assert.Matches(Heuristics.HeaderLine, "Remaining work:");
        Assert.DoesNotMatch(Heuristics.HeaderLine, "Remaining work: with more detail after it");
    }

    [Fact]
    public void Identifier_patterns_extract_paths_hex_versions_and_sizes()
    {
        const string text = "Saved C:\\work\\demo\\MEMORY.md; patched 0x00401230 in src/net/packet.cpp; "
                            + "version 1.0.0, header 128 bytes after 120 ms.";

        Assert.Contains(@"C:\work\demo\MEMORY.md", Heuristics.Path.Matches(text).Select(m => m.Value));
        // A bare relative path has no leading separator, so the reference path regex ignores it.
        Assert.DoesNotContain("src/net/packet.cpp", Heuristics.Path.Matches(text).Select(m => m.Value));
        Assert.Contains("0x00401230", Heuristics.Hex.Matches(text).Select(m => m.Value));
        Assert.Contains("1.0.0", Heuristics.Version.Matches(text).Select(m => m.Value));
        Assert.Contains("128 bytes", Heuristics.Size.Matches(text).Select(m => m.Value));
        Assert.Contains("120 ms", Heuristics.Size.Matches(text).Select(m => m.Value));
    }

    [Fact]
    public void IsHexLiteral_accepts_only_a_whole_hex_token()
    {
        Assert.True(Heuristics.IsHexLiteral("0x1F"));
        Assert.True(Heuristics.IsHexLiteral("0x00401230"));
        Assert.False(Heuristics.IsHexLiteral("1F"));
        Assert.False(Heuristics.IsHexLiteral("0x1F extra"));
    }
}
