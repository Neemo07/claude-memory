using System.Globalization;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>History retention and naming, ported from claude_memory.py:1715-1731.</summary>
public sealed class HistoryArchiverTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Creates_the_history_folder_next_to_the_output()
    {
        using var folder = new TempFolder();
        var target = folder.File("MEMORY.md");

        HistoryArchiver.Archive(target, "previous", 5, Now);

        var history = Path.Combine(folder.Path, "MEMORY.history");
        Assert.True(Directory.Exists(history));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(history, "20261104-151155-1.md")));
    }

    [Fact]
    public void Keeps_n_versions_and_prunes_the_oldest()
    {
        using var folder = new TempFolder();
        var target = folder.File("MEMORY.md");

        for (var index = 1; index <= 5; index++)
        {
            HistoryArchiver.Archive(target, "v" + index, 3, Now.AddMinutes(index));
        }

        var files = Directory.GetFiles(Path.Combine(folder.Path, "MEMORY.history"), "*.md");
        Assert.Equal(3, files.Length);

        var contents = files.Select(File.ReadAllText).OrderBy(text => text, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "v3", "v4", "v5" }, contents);
    }

    [Fact]
    public void Same_second_archives_get_an_incrementing_index()
    {
        using var folder = new TempFolder();
        var target = folder.File("MEMORY.md");

        HistoryArchiver.Archive(target, "first", 5, Now);
        HistoryArchiver.Archive(target, "second", 5, Now);

        var history = Path.Combine(folder.Path, "MEMORY.history");
        Assert.Equal("first", File.ReadAllText(Path.Combine(history, "20261104-151155-1.md")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(history, "20261104-151155-2.md")));
    }

    [Fact]
    public void Keep_zero_writes_nothing()
    {
        using var folder = new TempFolder();
        var target = folder.File("MEMORY.md");

        HistoryArchiver.Archive(target, "previous", 0, Now);

        Assert.False(Directory.Exists(Path.Combine(folder.Path, "MEMORY.history")));
    }
}