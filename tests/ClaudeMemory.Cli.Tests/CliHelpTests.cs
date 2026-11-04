using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// Help and error layout (claude_memory.py:1795-1804, argparse defaults): the golden
/// <c>cli-help.txt</c> is byte-identical after substituting the machine's default root, and an
/// invalid command line prints the usage block followed by the <c>prog: error:</c> line.
/// </summary>
public sealed class CliHelpTests
{
    [Fact]
    public void Help_matches_the_frozen_reference()
    {
        var specs = CliSpecs.Create(CliHarness.Now);
        var actual = CliHelp.Render(specs, MemoryOptions.DefaultRoot(), CliHarness.HelpWidth);

        var expected = Golden.ReadGolden("cli-help.txt")
            .Replace("%DEFAULT_ROOT%", MemoryOptions.DefaultRoot(), StringComparison.Ordinal);

        Assert.True(expected == actual, Golden.Diff("cli-help.txt", expected, actual));
    }

    [Fact]
    public void Help_starts_with_the_reference_usage_line()
    {
        var specs = CliSpecs.Create(CliHarness.Now);
        var text = CliHelp.Render(specs, MemoryOptions.DefaultRoot(), CliHarness.HelpWidth);

        Assert.StartsWith("usage: claude_memory.py [-h] [--version] [--root ROOT]\n", text, StringComparison.Ordinal);
        Assert.Contains("\noptions:\n", text, StringComparison.Ordinal);
        Assert.EndsWith("  claude_memory.py --list\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_default_root_follows_the_host_home()
    {
        var specs = CliSpecs.Create(CliHarness.Now);
        var text = CliHelp.Render(specs, @"C:\somewhere\.claude", CliHarness.HelpWidth);

        Assert.Contains("Claude config dir (default: C:\\somewhere\\.claude)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_arguments_print_usage_then_the_error_line()
    {
        var code = CliHarness.Run(new[] { "--bogus" }, out var stdout, out var stderr);

        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.StartsWith("usage: claude_memory.py [-h] [--version] [--root ROOT]\n", stderr, StringComparison.Ordinal);
        Assert.EndsWith(
            "claude_memory.py: error: unrecognized arguments: --bogus\n", stderr, StringComparison.Ordinal);

        // The reference emits 16 wrapped usage lines plus the error line.
        Assert.Equal(17, stderr.Split('\n').Length - 1);
    }

    [Fact]
    public void The_usage_block_has_one_line_per_wrapped_row()
    {
        var specs = CliSpecs.Create(CliHarness.Now);
        var usageLines = CliHelp.Usage(specs, CliHarness.HelpWidth).Split('\n');

        Assert.Equal(16, usageLines.Length);
        Assert.All(usageLines.Skip(1), line => Assert.StartsWith("                        ", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Help_writes_to_stdout_with_exit_zero()
    {
        var code = CliHarness.Run(new[] { "--help" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.StartsWith("usage: claude_memory.py", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_prints_the_reference_identity()
    {
        var code = CliHarness.Run(new[] { "--version" }, out var stdout, out var stderr);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal("claude_memory.py 1.1.0\n", stdout);
    }

    [Fact]
    public void Help_width_wraps_at_columns_minus_two()
    {
        var specs = CliSpecs.Create(CliHarness.Now);
        var narrow = CliHelp.Usage(specs, 40).Split('\n');
        var wide = CliHelp.Usage(specs, CliHarness.HelpWidth).Split('\n');

        Assert.True(narrow.Length > wide.Length, "a narrower terminal should wrap the usage more");
    }
}
