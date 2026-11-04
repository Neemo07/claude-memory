using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>Save-path refusal rules, ported from gui.py:1076-1088.</summary>
public sealed class PathPolicyTests
{
    [Fact]
    public void Resolves_an_absolute_folder_and_plain_name()
    {
        using var folder = new TempFolder();
        var target = PathPolicy.ResolveSavePath("MEMORY.md", folder.Path, folder.Path);

        Assert.Equal(Path.Combine(folder.Path, "MEMORY.md"), target);
    }

    [Fact]
    public void Defaults_the_name_when_none_is_supplied()
    {
        using var folder = new TempFolder();
        Assert.Equal("MEMORY.md", Path.GetFileName(PathPolicy.ResolveSavePath("  ", folder.Path, folder.Path)));
        Assert.Equal("MEMORY.md", Path.GetFileName(PathPolicy.ResolveSavePath(null, folder.Path, folder.Path)));
    }

    [Fact]
    public void Refuses_a_relative_folder()
    {
        using var folder = new TempFolder();
        var error = Assert.Throws<SaveException>(
            () => PathPolicy.ResolveSavePath("MEMORY.md", "relative\\dir", folder.Path));
        Assert.Contains("absolute path", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sub\\MEMORY.md")]
    [InlineData("sub/MEMORY.md")]
    public void Refuses_a_name_that_contains_a_path(string name)
    {
        using var folder = new TempFolder();
        var error = Assert.Throws<SaveException>(
            () => PathPolicy.ResolveSavePath(name, folder.Path, folder.Path));
        Assert.Contains("must not contain a path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_an_unsupported_suffix()
    {
        using var folder = new TempFolder();
        var error = Assert.Throws<SaveException>(
            () => PathPolicy.ResolveSavePath("MEMORY.exe", folder.Path, folder.Path));
        Assert.Contains(".md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_as_appends_md_for_an_unsupported_suffix()
    {
        Assert.Equal("MEMORY.md", PathPolicy.WithAllowedSuffix("MEMORY"));
        Assert.Equal("note.json", PathPolicy.WithAllowedSuffix("note.json"));
        Assert.Equal("NOTE.MARKDOWN", PathPolicy.WithAllowedSuffix("NOTE.MARKDOWN"));
    }

    [Fact]
    public void Expands_percent_and_dollar_variables()
    {
        var name = "CLAUDE_MEMORY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "C:\\expanded");
        try
        {
            Assert.Equal("C:\\expanded\\x", PathPolicy.ExpandVariables("%" + name + "%\\x"));
            Assert.Equal("C:\\expanded\\x", PathPolicy.ExpandVariables("$" + name + "\\x"));
            Assert.Equal("C:\\expanded\\x", PathPolicy.ExpandVariables("${" + name + "}\\x"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Leaves_unknown_variables_untouched()
    {
        Assert.Equal("%NO_SUCH_VARIABLE_9x%\\x", PathPolicy.ExpandVariables("%NO_SUCH_VARIABLE_9x%\\x"));
    }
}
