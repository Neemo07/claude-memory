using System.IO;
using ClaudeMemory.App.Services;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// Command-line routing (PRD FR-23, §9.25): a bad argument must explain itself and still open the
/// window; only a real Markdown file switches to the preview.
/// </summary>
public sealed class StartupRoutingTests
{
    [Fact]
    public void No_arguments_show_the_window()
    {
        var route = StartupRouting.Parse(Array.Empty<string>());

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Null(route.Message);
        Assert.Equal("", route.Path);
    }

    [Fact]
    public void A_null_argument_list_shows_the_window()
    {
        var route = StartupRouting.Parse(null);

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Null(route.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_argument_shows_the_window_silently(string argument)
    {
        var route = StartupRouting.Parse(new[] { argument });

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Null(route.Message);
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.markdown")]
    [InlineData("NOTES.MD")]
    public void An_existing_markdown_file_opens_in_the_preview(string name)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, "# Title\n");

        var route = StartupRouting.Parse(new[] { path });

        Assert.Equal(StartupAction.OpenFile, route.Action);
        Assert.Equal(path, route.Path);
        Assert.Null(route.Message);
    }

    [Fact]
    public void A_missing_file_reports_its_path_and_shows_the_window()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "gone.md");

        var route = StartupRouting.Parse(new[] { path });

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Equal("cannot open \"" + path + "\": no such file", route.Message);
    }

    [Fact]
    public void A_folder_is_refused_with_its_own_message()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "docs.md");
        Directory.CreateDirectory(folder);

        var route = StartupRouting.Parse(new[] { folder });

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Equal("cannot open \"" + folder + "\": that is a folder", route.Message);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("payload.json")]
    [InlineData("noext")]
    public void A_non_markdown_file_is_refused(string name)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, "content");

        var route = StartupRouting.Parse(new[] { path });

        Assert.Equal(StartupAction.ShowWindow, route.Action);
        Assert.Equal(
            "cannot open \"" + path + "\": choose a Markdown file (.md or .markdown)",
            route.Message);
    }

    [Fact]
    public void Only_the_first_argument_is_used()
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Path, "first.md");
        var second = Path.Combine(temp.Path, "second.md");
        File.WriteAllText(first, "# One\n");
        File.WriteAllText(second, "# Two\n");

        var route = StartupRouting.Parse(new[] { first, second });

        Assert.Equal(StartupAction.OpenFile, route.Action);
        Assert.Equal(first, route.Path);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "env.md");
        File.WriteAllText(path, "# Env\n");
        Environment.SetEnvironmentVariable("CLAUDE_MEMORY_ROUTE_TEST", temp.Path);

        try
        {
            var route = StartupRouting.Parse(
                new[] { Path.Combine("%CLAUDE_MEMORY_ROUTE_TEST%", "env.md") });

            Assert.Equal(StartupAction.OpenFile, route.Action);
            Assert.Equal(path, route.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_MEMORY_ROUTE_TEST", null);
        }
    }
}
