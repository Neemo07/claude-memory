using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeMemory.Cli;

/// <summary>
/// Reproduces argparse's help layout for the reference parser
/// (claude_memory.py:1795-1867) using <c>RawDescriptionHelpFormatter</c>.
/// </summary>
/// <remarks>
/// argparse wraps the usage at <c>COLUMNS - 2</c> and aligns continuation lines under the
/// program name. The options section puts help text at column 24 when the longest invocation
/// fits, and wraps it to <c>width - 24</c>. <c>--root</c>'s default is interpolated at render
/// time, so the golden records it as <c>{root}</c> instead of this machine's home directory.
/// </remarks>
internal static class CliHelp
{
    /// <summary>
    /// Program name shown in usage, error and version lines. The reference printed its own
    /// file name (<c>claude_memory.py</c>); this port prints its executable, the analogue of
    /// argparse deriving <c>prog</c> from <c>sys.argv[0]</c> (see deviation 16).
    /// </summary>
    public const string Prog = "ClaudeMemory.Cli";

    private const string UsagePrefix = "usage: ";

    private const string Description =
        "Summarise Claude Code sessions into a rolling markdown memory file.";

    private const string Epilog =
        "examples:\n"
        + "  " + Prog + " --out memory/MEMORY.md --sessions 5 --since 7d\n"
        + "  " + Prog + " --format claude-md --print\n"
        + "  " + Prog + " --list\n";

    /// <summary>argparse's default <c>max_help_position</c>.</summary>
    private const int MaxHelpPosition = 24;

    private const int SectionIndent = 2;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Terminal width minus the two columns argparse reserves. <c>COLUMNS</c> wins, then the
    /// real console width, then the 80-column fallback used when output is redirected.
    /// </summary>
    public static int DetectWidth()
    {
        var configured = Environment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(configured, out var columns) && columns > 0) return Math.Max(columns - 2, 11);

        if (!Console.IsOutputRedirected)
        {
            try
            {
                if (Console.WindowWidth > 0) return Math.Max(Console.WindowWidth - 2, 11);
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return 80 - 2;
    }

    /// <summary>The wrapped <c>usage:</c> block, without a trailing newline.</summary>
    public static string Usage(IReadOnlyList<OptionSpec> specs, int width)
    {
        var parts = new List<string> { Prog };
        foreach (var spec in specs) parts.Add(UsagePart(spec));

        // argparse aligns continuation lines under the program name and measures the first
        // line as if it were already indented.
        var indent = UsagePrefix.Length + Prog.Length + 1;
        var lines = new List<string>();
        var current = new List<string>();
        var length = UsagePrefix.Length - 1;

        foreach (var part in parts)
        {
            if (length + 1 + part.Length > width && current.Count > 0)
            {
                lines.Add(new string(' ', indent) + string.Join(" ", current));
                current.Clear();
                length = indent - 1;
            }

            current.Add(part);
            length += 1 + part.Length;
        }

        if (current.Count > 0) lines.Add(new string(' ', indent) + string.Join(" ", current));

        lines[0] = lines[0][indent..];
        return UsagePrefix + string.Join("\n", lines);
    }

    /// <summary>The full <c>--help</c> text, ending with a single newline.</summary>
    public static string Render(
        IReadOnlyList<OptionSpec> specs, string defaultRoot, int width)
    {
        var helpPosition = Math.Min(
            specs.Max(spec => Invocation(spec).Length) + 2, MaxHelpPosition);

        var builder = new StringBuilder();
        builder.Append(Usage(specs, width));
        builder.Append("\n\n").Append(Description).Append("\n\n");
        builder.Append("options:\n");

        foreach (var spec in specs) AppendAction(builder, spec, defaultRoot, width, helpPosition);

        builder.Append('\n').Append(Epilog);
        return builder.ToString();
    }

    private static void AppendAction(
        StringBuilder builder, OptionSpec spec, string defaultRoot, int width, int helpPosition)
    {
        var invocation = Invocation(spec);
        var help = spec.Help
            .Replace("{default}", spec.DefaultText ?? "", StringComparison.Ordinal)
            .Replace("{root}", defaultRoot, StringComparison.Ordinal);
        var lines = Wrap(help, Math.Max(width - helpPosition, 11));

        var actionWidth = helpPosition - SectionIndent - 2;
        if (invocation.Length <= actionWidth)
        {
            builder.Append(' ', SectionIndent)
                .Append(invocation.PadRight(actionWidth))
                .Append("  ")
                .Append(lines.Count > 0 ? lines[0] : "")
                .Append('\n');
        }
        else
        {
            builder.Append(' ', SectionIndent).Append(invocation).Append('\n');
            builder.Append(' ', helpPosition).Append(lines.Count > 0 ? lines[0] : "").Append('\n');
        }

        for (var index = 1; index < lines.Count; index++)
        {
            builder.Append(' ', helpPosition).Append(lines[index]).Append('\n');
        }
    }

    private static string UsagePart(OptionSpec spec) => spec.Kind == OptionKind.Value
        ? "[" + spec.Names[0] + " " + ValueMetavar(spec) + "]"
        : "[" + spec.Names[0] + "]";

    private static string Invocation(OptionSpec spec) => spec.Kind == OptionKind.Value
        ? string.Join(", ", spec.Names.Select(name => name + " " + ValueMetavar(spec)))
        : string.Join(", ", spec.Names);

    private static string ValueMetavar(OptionSpec spec) => spec.Choices is { } choices
        ? "{" + string.Join(",", choices) + "}"
        : spec.Metavar;

    /// <summary>
    /// Python <c>textwrap.wrap(text, width)</c> with argparse's whitespace collapsing: greedy
    /// fill, and words break after a hyphen when the next character is a letter.
    /// </summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var collapsed = WhitespaceRun.Replace(text, " ").Trim();
        if (collapsed.Length == 0) return lines;

        var current = new StringBuilder();
        var currentLength = 0;

        foreach (var word in collapsed.Split(' '))
        {
            var first = true;
            foreach (var piece in HyphenPieces(word))
            {
                var separator = first && currentLength > 0 ? 1 : 0;
                first = false;

                if (currentLength > 0 && currentLength + separator + piece.Length > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    currentLength = 0;
                    separator = 0;
                }

                if (currentLength == 0 && piece.Length > width)
                {
                    var rest = piece;
                    while (rest.Length > width)
                    {
                        lines.Add(rest[..width]);
                        rest = rest[width..];
                    }

                    current.Append(rest);
                    currentLength = rest.Length;
                    continue;
                }

                if (separator == 1) current.Append(' ');
                current.Append(piece);
                currentLength += separator + piece.Length;
            }
        }

        if (currentLength > 0) lines.Add(current.ToString());
        return lines;
    }

    /// <summary>
    /// Splits a word at hyphens the way textwrap's <c>wordsep_re</c> does: the hyphen stays
    /// with the left piece and the next character must be a letter.
    /// </summary>
    private static IEnumerable<string> HyphenPieces(string word)
    {
        var start = 0;
        var index = 0;
        while (index < word.Length)
        {
            if (word[index] != '-')
            {
                index++;
                continue;
            }

            var run = index;
            while (run < word.Length && word[run] == '-') run++;

            if (index > 0 && IsWordCharacter(word[index - 1]) && run < word.Length && char.IsLetter(word[run]))
            {
                yield return word[start..run];
                start = run;
            }

            index = run;
        }

        yield return word[start..];
    }

    private static bool IsWordCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_' || value is '!' or '"' or '\'' or '&' or '.' or ',' or '?';
}