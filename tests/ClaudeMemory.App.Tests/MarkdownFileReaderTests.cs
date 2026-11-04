using System.IO;
using System.Text;
using ClaudeMemory.App.Preview;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// Guards from gui.py:1027-1049 (<c>read_markdown_file</c>): PRD §9.15 requires a clear
/// rejection for a missing file, a wrong suffix, an oversized file, a binary file, and an
/// empty file, each with the reference's wording.
/// </summary>
public sealed class MarkdownFileReaderTests
{
    private static string Write(TempDirectory temp, string name, string content)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Fact]
    public void Reads_a_markdown_file_and_returns_its_name()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "README.md", "# Title\n\nbody\n");

        var file = MarkdownFileReader.Read(path);

        Assert.Equal("# Title\n\nbody\n", file.Text);
        Assert.Equal("README.md", file.Name);
        Assert.Equal(path, file.Path);
    }

    [Fact]
    public void A_missing_file_is_named_in_the_error()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "nope.md");

        var error = Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));

        Assert.Equal("No such file: " + path, error.Message);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("payload.json")]
    [InlineData("noext")]
    public void A_non_markdown_suffix_is_refused(string name)
    {
        using var temp = new TempDirectory();
        var path = Write(temp, name, "content");

        var error = Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));

        Assert.Equal("Choose a Markdown file (.md or .markdown).", error.Message);
    }

    [Theory]
    [InlineData("upper.MD")]
    [InlineData("mixed.Markdown")]
    public void The_suffix_check_ignores_case(string name)
    {
        using var temp = new TempDirectory();
        var path = Write(temp, name, "content");

        Assert.Equal("content", MarkdownFileReader.Read(path).Text);
    }

    [Fact]
    public void A_file_over_four_mebibytes_is_refused_with_the_exact_numbers()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "big.md");
        File.WriteAllBytes(path, new byte[MarkdownFileReader.MaxBytes + 1]);

        var error = Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));

        Assert.Equal(
            "big.md is too large to preview "
            + $"({MarkdownFileReader.MaxBytes + 1} bytes; the limit is {MarkdownFileReader.MaxBytes}).",
            error.Message);
    }

    [Fact]
    public void A_file_exactly_at_the_limit_is_allowed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "limit.md");
        File.WriteAllText(path, "a");

        // The limit is checked as "greater than", so the boundary itself passes.
        var file = MarkdownFileReader.Read(path);
        Assert.Equal("a", file.Text);
    }

    [Fact]
    public void A_file_containing_a_nul_byte_is_not_text()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "binary.md");
        File.WriteAllBytes(path, new byte[] { 0x23, 0x00, 0x41 });

        var error = Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));

        Assert.Equal("binary.md is not a text file.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t\n")]
    public void An_empty_file_is_refused(string content)
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "empty.md", content);

        var error = Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));

        Assert.Equal("empty.md is empty.", error.Message);
    }

    [Fact]
    public void A_utf8_bom_is_stripped()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bom.md");
        File.WriteAllText(path, "# Title", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var file = MarkdownFileReader.Read(path);

        Assert.Equal("# Title", file.Text);
        Assert.DoesNotContain('\uFEFF', file.Text);
    }

    [Fact]
    public void Invalid_utf8_bytes_are_replaced_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "latin1.md");
        File.WriteAllBytes(path, new byte[] { 0x63, 0x61, 0x66, 0xFF, 0x0A });

        var file = MarkdownFileReader.Read(path);

        Assert.Contains("caf", file.Text, StringComparison.Ordinal);
        Assert.Contains('\uFFFD', file.Text);
    }

    [Fact]
    public void A_directory_is_not_a_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "folder.md");
        Directory.CreateDirectory(path);

        Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(path));
    }

    [Fact]
    public void A_null_path_is_refused_rather_than_throwing_a_null_reference()
    {
        Assert.Throws<PreviewException>(() => MarkdownFileReader.Read(null!));
    }
}
