using System.Text;

namespace ClaudeMemory.Cli;

/// <summary>A command-line error reported the way argparse reports one (exit code 2).</summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// A conversion or choice failure. The parser prepends the canonical option name, so the
/// message here starts at <c>invalid …</c> (argparse <c>ArgumentError</c>).
/// </summary>
internal sealed class CliValueException : Exception
{
    public CliValueException(string message) : base(message)
    {
    }
}

/// <summary>
/// Python's <c>int(text)</c>, which argparse uses for every numeric option
/// (claude_memory.py:1819, 1832-1848). Unlike <see cref="int.Parse(string)"/> it ignores
/// surrounding whitespace and accepts digit-grouping underscores.
/// </summary>
internal static class PyInt
{
    public static int Parse(string text)
    {
        if (!TryParse(text, out var value))
        {
            throw new CliValueException($"invalid int value: {PyText.Repr(text)}");
        }

        return value;
    }

    public static bool TryParse(string? text, out int value)
    {
        value = 0;

        var body = (text ?? "").Trim();
        var index = 0;
        var negative = false;
        if (index < body.Length && (body[index] == '+' || body[index] == '-'))
        {
            negative = body[index] == '-';
            index++;
        }

        long total = 0;
        var digits = 0;
        var previousWasDigit = false;

        for (; index < body.Length; index++)
        {
            var current = body[index];
            if (current == '_')
            {
                // Python allows a single underscore between digits only.
                if (!previousWasDigit || index + 1 >= body.Length) return false;
                previousWasDigit = false;
                continue;
            }

            if (!char.IsDigit(current)) return false;

            var digit = (int)char.GetNumericValue(current);
            total = total > long.MaxValue / 10 ? long.MaxValue : total * 10 + digit;
            digits++;
            previousWasDigit = true;
        }

        if (digits == 0 || !previousWasDigit) return false;

        var signed = negative ? -total : total;
        value = signed switch
        {
            > int.MaxValue => int.MaxValue,
            < int.MinValue => int.MinValue,
            _ => (int)signed,
        };

        return true;
    }
}

/// <summary>Python <c>repr()</c> for the single-line values used in error messages.</summary>
internal static class PyText
{
    public static string Repr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var body = new StringBuilder(value.Length + 2);
        foreach (var current in value)
        {
            if (current == '\\' || current == quote) body.Append('\\');
            body.Append(current);
        }

        return quote + body.ToString() + quote;
    }
}