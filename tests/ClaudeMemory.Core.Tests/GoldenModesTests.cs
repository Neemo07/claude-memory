using System.Globalization;
using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Byte-parity against the frozen Python 1.1.0 outputs for the remaining modes: <c>full</c>,
/// <c>transcript</c> and <c>json</c>. Only the allowed normalisations apply
/// (IMPLEMENTATION_PLAN.md §2).
/// </summary>
public sealed class GoldenModesTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Full_memory_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        var actual = FullMemoryBuilder.Build(MemoryPipeline.Load(opts), opts, Now);
        AssertMatches("full-plain.md", actual);
    }

    [Fact]
    public void Transcript_matches_the_reference_golden()
    {
        var opts = MemoryPipeline.NewOptions();
        var actual = TranscriptBuilder.Build(MemoryPipeline.Load(opts), opts, Now);
        AssertMatches("transcript.md", actual);
    }

    [Fact]
    public void Json_matches_the_reference_golden()
    {
        var actual = JsonExporter.Build(MemoryPipeline.Load(MemoryPipeline.NewOptions()), Now);
        var expected = Golden.NormaliseJson(File.ReadAllText(Path.Combine(TestPaths.Golden, "json.json")));
        var normalised = Golden.NormaliseJson(actual);

        Assert.True(expected == normalised, Golden.Diff("json.json", expected, normalised));
    }

    [Fact]
    public void Json_keeps_non_ascii_unescaped_and_round_trips()
    {
        const string arabic = "\u0645\u0631\u062d\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645";
        var session = new Session("C:/p/x.jsonl") { Sid = "s", Cwd = "C:/p" };
        session.Turns.Add(new Turn { Role = TurnRoles.User, Text = arabic });

        var text = JsonExporter.Build(new[] { session }, Now);

        Assert.Contains(arabic, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0645", text, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(text);
        var parsed = document.RootElement
            .GetProperty("sessions")[0]
            .GetProperty("messages")[0]
            .GetProperty("text")
            .GetString();
        Assert.Equal(arabic, parsed);
    }

    private static void AssertMatches(string goldenName, string actual)
    {
        var expected = Golden.Normalise(File.ReadAllText(Path.Combine(TestPaths.Golden, goldenName)));
        var normalised = Golden.Normalise(actual);

        Assert.True(expected == normalised, Golden.Diff(goldenName, expected, normalised));
    }
}
