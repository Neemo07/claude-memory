using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.Core.Tests;

/// <summary>The <c>--list</c> table, ported from claude_memory.py:1734-1753.</summary>
public sealed class ListRendererTests
{
    [Fact]
    public void List_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        var actual = ListRenderer.Build(MemoryPipeline.LoadAll(opts), opts);

        var expected = Golden.Normalise(File.ReadAllText(Path.Combine(TestPaths.Golden, "list.md")));
        var normalised = Golden.Normalise(actual);
        Assert.True(expected == normalised, Golden.Diff("list.md", expected, normalised));
    }

    [Fact]
    public void Empty_list_uses_the_reference_message()
    {
        var text = ListRenderer.Build(Array.Empty<Session>(), new MemoryOptions());

        Assert.Equal(ListRenderer.EmptyMessage, text);
        Assert.Contains("No Claude Code transcripts found.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Size_rounds_halves_to_even_like_python()
    {
        using var folder = new TempFolder();
        var half = folder.File("half.jsonl");
        var oneAndHalf = folder.File("oneandhalf.jsonl");
        File.WriteAllBytes(half, new byte[512]);
        File.WriteAllBytes(oneAndHalf, new byte[1536]);

        var text = ListRenderer.Build(
            new[] { new Session(half) { Cwd = "C:/p" }, new Session(oneAndHalf) { Cwd = "C:/p" } },
            new MemoryOptions());

        // Python format(0.5, ".0f") is "0" and format(1.5, ".0f") is "2" (round half to even).
        Assert.Contains("| 0 KB |", text, StringComparison.Ordinal);
        Assert.Contains("| 2 KB |", text, StringComparison.Ordinal);
    }
}