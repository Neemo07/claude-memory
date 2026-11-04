using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// The argparse-compatible parser (claude_memory.py:1795-1872). Every expectation here was
/// verified against <c>py -3.12 claude_memory.py</c> on this machine; see IMPLEMENTATION_PLAN.md
/// Phase 5.
/// </summary>
public sealed class CliParserTests
{
    private static IReadOnlyList<OptionSpec> Specs() => CliSpecs.Create(CliHarness.Now);

    private static CliParseResult Parse(params string[] args)
        => CliParser.Parse(args, Specs(), new MemoryOptions());

    [Theory]
    [InlineData("--bogus", "unrecognized arguments: --bogus")]
    [InlineData("foo", "unrecognized arguments: foo")]
    [InlineData("-x", "unrecognized arguments: -x")]
    [InlineData("-", "unrecognized arguments: -")]
    [InlineData("--", "unrecognized arguments: --")]
    [InlineData("--bogus=1", "unrecognized arguments: --bogus=1")]
    public void Unknown_tokens_are_reported_as_unrecognized(string argument, string expected)
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(argument));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Everything_after_a_double_dash_is_unrecognized_including_the_marker()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--", "--mode", "full"));
        Assert.Equal("unrecognized arguments: -- --mode full", error.Message);
    }

    [Fact]
    public void A_double_dash_after_a_value_option_still_stops_processing()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--root", TestPaths.FixtureRoot, "--", "--list"));
        Assert.Equal("unrecognized arguments: -- --list", error.Message);
    }

    [Theory]
    [InlineData("--mode", "argument --mode: expected one argument")]
    [InlineData("--cwd", "argument --cwd: expected one argument")]
    [InlineData("--root", "argument --root: expected one argument")]
    public void A_value_option_without_a_value_is_rejected(string option, string expected)
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(option));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void A_flag_is_not_accepted_as_another_options_value()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--cwd", "-x"));
        Assert.Equal("argument --cwd: expected one argument", error.Message);
    }

    [Fact]
    public void A_double_dash_is_not_accepted_as_a_value()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--root", "--"));
        Assert.Equal("argument --root: expected one argument", error.Message);
    }

    [Theory]
    [InlineData("--mode", "nope", "argument --mode: invalid choice: 'nope' (choose from memory, full, transcript, json)")]
    [InlineData("--format", "md", "argument --format: invalid choice: 'md' (choose from plain, claude-md)")]
    [InlineData("--sidechains", "maybe", "argument --sidechains: invalid choice: 'maybe' (choose from keep, drop)")]
    [InlineData("--lang", "fr", "argument --lang: invalid choice: 'fr' (choose from auto, en, ar, both)")]
    public void Choice_failures_use_the_argparse_wording(string option, string value, string expected)
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(option, value));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void An_invalid_int_names_the_option_and_the_value()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--sessions", "x"));
        Assert.Equal("argument --sessions: invalid int value: 'x'", error.Message);
    }

    [Fact]
    public void An_unparseable_since_value_keeps_the_reference_message()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--since", "nope"));
        Assert.Equal("argument --since: cannot parse --since value: 'nope'", error.Message);
    }

    [Fact]
    public void An_ambiguous_long_option_lists_the_candidates_in_definition_order()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--max"));
        Assert.Equal(
            "ambiguous option: --max could match --max-chars, --max-items, --max-paths, "
            + "--max-cmds, --max-line, --max-prompts, --max-file-bytes",
            error.Message);
    }

    [Fact]
    public void A_short_ambiguous_prefix_lists_both_long_names()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--he"));
        Assert.Equal("ambiguous option: --he could match --help, --here", error.Message);
    }

    [Fact]
    public void Abbreviation_that_spans_two_value_options_is_ambiguous()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--max-c", "300"));
        Assert.Equal("ambiguous option: --max-c could match --max-chars, --max-cmds", error.Message);
    }

    [Theory]
    [InlineData("--help=x", "argument -h/--help: ignored explicit argument 'x'")]
    [InlineData("--version=1", "argument --version: ignored explicit argument '1'")]
    [InlineData("--print=1", "argument --print: ignored explicit argument '1'")]
    [InlineData("--list=1", "argument --list: ignored explicit argument '1'")]
    public void A_flag_with_an_inline_value_is_rejected(string argument, string expected)
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(argument));
        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData("-h", "foo")]
    [InlineData("-hh", "")]
    [InlineData("--help", "")]
    [InlineData("--bogus", "--help")]
    [InlineData("foo", "--help")]
    public void Help_wins_as_soon_as_it_is_seen(params string[] args)
    {
        Assert.Equal(CliAction.Help, Parse(args).Action);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--version", "foo")]
    [InlineData("--vers")]
    public void Version_is_an_immediate_action(params string[] args)
    {
        Assert.Equal(CliAction.Version, Parse(args).Action);
    }

    [Fact]
    public void An_error_before_help_still_wins()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse("--mode", "nope", "--help"));
        Assert.Equal(
            "argument --mode: invalid choice: 'nope' (choose from memory, full, transcript, json)",
            error.Message);
    }

    [Fact]
    public void Unambiguous_abbreviations_are_accepted()
    {
        var result = Parse("--r", TestPaths.FixtureRoot, "--mod", "full", "--li");

        Assert.Equal(CliAction.Run, result.Action);
        Assert.Equal(TestPaths.FixtureRoot, result.Options.Root);
        Assert.Equal("full", result.Options.Mode);
        Assert.True(result.Options.ListOnly);
    }

    [Theory]
    [InlineData("--max-chars", "-5")]
    [InlineData("--max-chars=-5", null)]
    public void Negative_numbers_are_accepted_as_values(string option, string? value)
    {
        var result = value is null ? Parse(option) : Parse(option, value);
        Assert.Equal(-5, result.Options.MaxChars);
    }

    [Fact]
    public void Inline_equals_form_is_accepted()
    {
        var result = Parse("--mode=full", "--root=" + TestPaths.FixtureRoot);

        Assert.Equal("full", result.Options.Mode);
        Assert.Equal(TestPaths.FixtureRoot, result.Options.Root);
    }

    [Fact]
    public void Flags_set_their_option_and_print_defaults_to_off()
    {
        var result = Parse("--no-addresses", "--include-thinking", "--ascii", "--only-share", "--quiet");

        Assert.False(result.Options.IncludeAddresses);
        Assert.True(result.Options.IncludeThinking);
        Assert.True(result.Options.AsciiOnly);
        Assert.True(result.Options.OnlyShare);
        Assert.True(result.Options.Quiet);
    }

    [Fact]
    public void Since_accepts_a_relative_window_against_the_supplied_clock()
    {
        var result = Parse("--since", "7d");
        Assert.Equal(CliHarness.Now.AddDays(-7), result.Options.Since);
    }

    [Theory]
    [InlineData("1_0", 10)]
    [InlineData(" 5 ", 5)]
    [InlineData("+3", 3)]
    [InlineData("-5", -5)]
    [InlineData("0", 0)]
    public void Py_int_accepts_python_spellings(string text, int expected)
    {
        Assert.True(PyInt.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1_")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("+")]
    [InlineData("1.5")]
    public void Py_int_rejects_everything_else(string text)
    {
        Assert.False(PyInt.TryParse(text, out _));
    }

    [Fact]
    public void Py_int_treats_unicode_digits_as_digits()
    {
        // Arabic-Indic three, which Python's int() accepts.
        Assert.True(PyInt.TryParse("\u0663", out var value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Py_int_saturates_instead_of_overflowing()
    {
        Assert.True(PyInt.TryParse("99999999999999999999999999", out var value));
        Assert.Equal(int.MaxValue, value);
    }
}
