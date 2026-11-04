namespace ClaudeMemory.Core.Tests;

public sealed class GoldenOracleTests
{
    private static readonly string[] ExpectedGoldenFiles =
    [
        "memory-plain.md",
        "memory-claude-md.md",
        "memory-ascii.md",
        "memory-lang-both.md",
        "full-plain.md",
        "transcript.md",
        "json.json",
        "list.md",
        "share-memory.md",
    ];

    [Fact]
    public void Every_golden_file_exists_and_has_content()
    {
        Assert.True(Directory.Exists(TestPaths.Golden), "Missing golden directory: " + TestPaths.Golden);

        foreach (var name in ExpectedGoldenFiles)
        {
            var path = Path.Combine(TestPaths.Golden, name);
            Assert.True(File.Exists(path), "Missing golden file: " + name);
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)), "Empty golden file: " + name);
        }
    }

    [Fact]
    public void Memory_golden_keeps_the_reference_section_markers()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Golden, "memory-plain.md"));

        Assert.Contains("# Project memory", text, StringComparison.Ordinal);
        Assert.Contains("## Sessions", text, StringComparison.Ordinal);
        Assert.Contains("## What was asked", text, StringComparison.Ordinal);
        Assert.Contains("## Most recent exchange", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_md_golden_is_wrapped_in_reference_markers()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Golden, "memory-claude-md.md"));

        Assert.StartsWith("<!-- claude-memory:begin -->", text, StringComparison.Ordinal);
        Assert.EndsWith("<!-- claude-memory:end -->\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_golden_is_well_formed_and_names_the_reference_tool()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Golden, "json.json"));
        using var document = System.Text.Json.JsonDocument.Parse(text);

        Assert.True(document.RootElement.TryGetProperty("sessions", out var sessions));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, sessions.ValueKind);
        Assert.Contains("claude_memory.py 1.1.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_golden_uses_the_active_branch_only()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Golden, "share-memory.md"));

        Assert.Contains("ACTIVE BRANCH", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABANDONED BRANCH", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This block is hidden.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_corpus_covers_local_arabic_and_malformed_input()
    {
        var root = Path.Combine(TestPaths.Fixtures, "fixture_root", "projects");
        var transcripts = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);

        Assert.Contains(transcripts, path => path.EndsWith("synthetic.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transcripts, path => path.EndsWith("arabic.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transcripts, path => path.EndsWith("corrupt.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transcripts, path => new FileInfo(path).Length == 0);
        Assert.True(File.Exists(Path.Combine(TestPaths.Fixtures, "share-branching.json")));
    }
}