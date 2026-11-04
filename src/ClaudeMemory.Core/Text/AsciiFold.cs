using System.Text;

namespace ClaudeMemory.Core.Text;

/// <summary>
/// Legacy-console downgrade, ported from claude_memory.py:296-325
/// (<c>ASCII_MAP</c>, <c>to_ascii</c>).
/// </summary>
/// <remarks>
/// Only decoration is replaced. Letters in any script are preserved, because replacing them
/// used to turn Arabic into "?????", which looked like a bug in the generator, not the switch.
/// </remarks>
public static class AsciiFold
{
    private static readonly (char Source, string Replacement)[] Map =
    {
        ('\u2192', "->"),
        ('\u00b7', "*"),
        ('\u00d7', "x"),
        ('\u2026', "..."),
        ('\u2014', "--"),
        ('\u2013', "-"),
        ('\u2022', "-"),
        ('\u2018', "'"),
        ('\u2019', "'"),
        ('\u201c', "\""),
        ('\u201d', "\""),
        ('\u2265', ">="),
        ('\u2264', "<="),
        ('\u2713', "[ok]"),
        ('\u2717', "[x]"),
        ('\u2588', "#"),
        ('\u2500', "-"),
        ('\u00a0', " "),
    };

    /// <summary>Downgrades typographic glyphs; keeps letters and escapes unknown symbols.</summary>
    public static string ToAscii(string text)
    {
        var folded = text;
        foreach (var (source, replacement) in Map) folded = folded.Replace(source.ToString(), replacement);

        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (ch <= '\u007f') builder.Append(ch);
            else if (char.IsLetter(ch)) builder.Append(ch);
            else builder.Append('?');
        }

        return builder.ToString();
    }
}
