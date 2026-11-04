using System.Text;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>Atomic write behaviour, ported from claude_memory.py:1708-1712.</summary>
public sealed class AtomicFileWriterTests
{
    [Fact]
    public void Writes_utf8_without_bom_and_with_lf_newlines()
    {
        using var folder = new TempFolder();
        var target = folder.File("out.md");

        AtomicFileWriter.Write(target, "line1\nline2\n\u0645\u0631\u062d\u0628\u0627\n");

        var bytes = File.ReadAllBytes(target);
        Assert.Equal((byte)'l', bytes[0]); // no BOM
        Assert.Equal("line1\nline2\n\u0645\u0631\u062d\u0628\u0627\n", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain('\r', Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Replaces_an_existing_file_and_leaves_no_temp()
    {
        using var folder = new TempFolder();
        var target = folder.File("out.md");
        File.WriteAllText(target, "old content that is longer");

        AtomicFileWriter.Write(target, "new");

        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void Creates_a_missing_directory()
    {
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, "nested", "depth", "out.md");

        AtomicFileWriter.Write(target, "x");

        Assert.Equal("x", File.ReadAllText(target));
    }

    [Fact]
    public void Cancellation_leaves_no_temp_file()
    {
        using var folder = new TempFolder();
        var target = folder.File("out.md");
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AtomicFileWriter.Write(target, "x", source.Token));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }
}
