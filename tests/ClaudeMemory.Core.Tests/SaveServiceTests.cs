using System.Globalization;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>Save orchestration, ported from gui.py:1091-1103 and 1176-1205.</summary>
public sealed class SaveServiceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Save_writes_the_file_and_returns_its_path()
    {
        using var folder = new TempFolder();

        var target = SaveService.Save("MEMORY.md", folder.Path, "# Memory\n", 0, Now);

        Assert.Equal(Path.Combine(folder.Path, "MEMORY.md"), target);
        Assert.Equal("# Memory\n", File.ReadAllText(target));
    }

    [Fact]
    public void Save_archives_the_previous_content_when_history_is_enabled()
    {
        using var folder = new TempFolder();
        SaveService.Save("MEMORY.md", folder.Path, "old", 2, Now);

        SaveService.Save("MEMORY.md", folder.Path, "new", 2, Now.AddMinutes(1));

        Assert.Equal("new", File.ReadAllText(Path.Combine(folder.Path, "MEMORY.md")));
        var history = Path.Combine(folder.Path, "MEMORY.history");
        var archived = Directory.GetFiles(history, "*.md");
        Assert.Single(archived);
        Assert.Equal("old", File.ReadAllText(archived[0]));
    }

    [Fact]
    public void Save_without_history_overwrites_and_writes_no_history_folder()
    {
        using var folder = new TempFolder();
        SaveService.Save("MEMORY.md", folder.Path, "old", 0, Now);

        SaveService.Save("MEMORY.md", folder.Path, "new", 0, Now.AddMinutes(1));

        Assert.Equal("new", File.ReadAllText(Path.Combine(folder.Path, "MEMORY.md")));
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "MEMORY.history")));
    }

    [Fact]
    public void Save_refuses_an_unsupported_suffix()
    {
        using var folder = new TempFolder();
        Assert.Throws<SaveException>(
            () => SaveService.Save("MEMORY.exe", folder.Path, "x", 0, Now));
    }

    [Fact]
    public void Save_refuses_empty_text()
    {
        using var folder = new TempFolder();
        var error = Assert.Throws<SaveException>(
            () => SaveService.Save("MEMORY.md", folder.Path, "   \n", 0, Now));
        Assert.Contains("nothing to save", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_as_appends_md_to_an_unsupported_path()
    {
        using var folder = new TempFolder();
        var chosen = Path.Combine(folder.Path, "note");

        var target = SaveService.SaveAs(chosen, "x", 0, Now);

        Assert.Equal(chosen + ".md", target);
        Assert.Equal("x", File.ReadAllText(target));
    }

    [Fact]
    public void Save_as_keeps_an_allowed_suffix()
    {
        using var folder = new TempFolder();
        var chosen = Path.Combine(folder.Path, "note.json");

        Assert.Equal(chosen, SaveService.SaveAs(chosen, "{}", 0, Now));
    }

    [Fact]
    public void Ensure_containing_folder_creates_it()
    {
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, "nested", "MEMORY.md");

        var containing = SaveService.EnsureContainingFolder(target);

        Assert.Equal(Path.Combine(folder.Path, "nested"), containing);
        Assert.True(Directory.Exists(containing));
    }

    [Fact]
    public void A_locked_target_fails_without_leaving_a_temp_file()
    {
        using var folder = new TempFolder();
        var target = SaveService.Save("MEMORY.md", folder.Path, "old", 0, Now);

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => SaveService.Save("MEMORY.md", folder.Path, "new", 0, Now));
            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException, error!.GetType().Name);
        }

        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void A_read_only_target_fails_without_leaving_a_temp_file()
    {
        using var folder = new TempFolder();
        var target = SaveService.Save("MEMORY.md", folder.Path, "old", 0, Now);
        File.SetAttributes(target, FileAttributes.ReadOnly);

        try
        {
            var error = Record.Exception(() => SaveService.Save("MEMORY.md", folder.Path, "new", 0, Now));
            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException, error!.GetType().Name);
            Assert.False(File.Exists(target + ".tmp"));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Cancellation_leaves_no_target_or_temp_file()
    {
        using var folder = new TempFolder();
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => SaveService.Save("MEMORY.md", folder.Path, "x", 0, Now, source.Token));

        Assert.False(File.Exists(Path.Combine(folder.Path, "MEMORY.md")));
        Assert.False(File.Exists(Path.Combine(folder.Path, "MEMORY.md.tmp")));
    }
}