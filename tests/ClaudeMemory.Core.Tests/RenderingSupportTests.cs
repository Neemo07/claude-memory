using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Vectors for the rendering support helpers: token estimate, ASCII fold and bilingual titles
/// (claude_memory.py:245-325, 158-204).
/// </summary>
public sealed class RenderingSupportTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcdefgh", 2)]
    public void Estimate_weights_ascii_at_four_chars_per_token(string text, int expected)
        => Assert.Equal(expected, TokenEstimator.Estimate(text));

    [Fact]
    public void Estimate_counts_arabic_at_its_own_rate()
    {
        // Five Arabic letters at 1.6 chars per token -> 3.125, rounded to 3.
        Assert.Equal(3, TokenEstimator.Estimate("\u0645\u0631\u062d\u0628\u0627"));
    }

    [Fact]
    public void ToAscii_downgrades_decoration_but_keeps_letters()
    {
        Assert.Equal("a -- b -> c", AsciiFold.ToAscii("a \u2014 b \u2192 c"));
        Assert.Equal("...", AsciiFold.ToAscii("\u2026"));
        Assert.Equal("1 - 2", AsciiFold.ToAscii("1 \u2013 2"));

        // Arabic letters survive the switch: this used to become "?????".
        Assert.Equal("\u0645\u0631\u062d\u0628\u0627", AsciiFold.ToAscii("\u0645\u0631\u062d\u0628\u0627"));

        // A symbol with no ASCII equivalent becomes '?', as before.
        Assert.Equal("?", AsciiFold.ToAscii("\u2603"));
    }

    [Fact]
    public void Bilingual_titles_use_the_requested_language()
    {
        var sessions = new List<Session> { new("C:/p/x.jsonl") };

        Assert.Equal("Sessions | \u0627\u0644\u062c\u0644\u0633\u0627\u062a", BilingualTitles.For("Sessions", sessions, "both"));
        Assert.Equal("Sessions", BilingualTitles.For("Sessions", sessions, "en"));
        Assert.Equal("\u0627\u0644\u062c\u0644\u0633\u0627\u062a", BilingualTitles.For("Sessions", sessions, "ar"));

        // "Compactor summaries" is present in the map even though render_summaries writes the
        // English heading directly and never calls bilingual_title.
        Assert.Equal("Compactor summaries | \u0645\u0644\u062e\u0635\u0627\u062a \u0627\u0644\u062c\u0644\u0633\u0627\u062a",
            BilingualTitles.For("Compactor summaries", sessions, "both"));

        // A title with no entry falls back to English in every mode.
        Assert.Equal("Stated preferences and conventions",
            BilingualTitles.For("Stated preferences and conventions", sessions, "both"));
    }

    [Fact]
    public void Bilingual_titles_return_english_when_there_are_no_sessions()
        => Assert.Equal("Sessions", BilingualTitles.For("Sessions", null, "both"));
}
