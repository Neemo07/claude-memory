using System.Globalization;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Rendering;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Byte-parity against the frozen Python 1.1.0 outputs for <c>--mode memory</c>.
/// The <c>_Generated ..._</c> line is the only content normalisation.
/// </summary>
public sealed class GoldenMemoryTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Plain_memory_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        var actual = SummaryBuilder.Build(MemoryPipeline.Load(opts), opts, Now);
        AssertMatches("memory-plain.md", actual);
    }

    [Fact]
    public void Claude_md_wrap_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        opts.Format = "claude-md";
        var actual = SummaryBuilder.Build(MemoryPipeline.Load(opts), opts, Now);
        AssertMatches("memory-claude-md.md", actual);
    }

    [Fact]
    public void Ascii_fold_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        var actual = AsciiFold.ToAscii(SummaryBuilder.Build(MemoryPipeline.Load(opts), opts, Now));
        AssertMatches("memory-ascii.md", actual);
    }

    [Fact]
    public void Bilingual_headers_match_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        opts.Lang = "both";
        var actual = SummaryBuilder.Build(MemoryPipeline.Load(opts), opts, Now);
        AssertMatches("memory-lang-both.md", actual);
    }

    private static void AssertMatches(string goldenName, string actual)
    {
        var expected = Golden.Normalise(File.ReadAllText(Path.Combine(TestPaths.Golden, goldenName)));
        var normalised = Golden.Normalise(actual);

        Assert.True(expected == normalised, Golden.Diff(goldenName, expected, normalised));
    }
}
