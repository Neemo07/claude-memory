using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Text;

/// <summary>
/// Arabic-script detection, ported from claude_memory.py:137-152
/// (<c>ARABIC_RX</c>, <c>has_arabic</c>, <c>arabic_ratio</c>).
/// </summary>
public static class ArabicText
{
    /// <summary>Arabic, Arabic Supplement, Arabic Extended-A and Extended-B (claude_memory.py:137).</summary>
    private static readonly Regex ArabicScript = new(
        "[\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF\u0870-\u089F]",
        RegexOptions.Compiled);

    /// <summary>True when the text contains at least one Arabic-script character.</summary>
    public static bool HasArabic(string? text) => ArabicScript.IsMatch(text ?? "");

    /// <summary>True when the character is in one of the Arabic-script ranges (claude_memory.py:137).</summary>
    public static bool IsArabic(char ch)
        => (ch >= '\u0600' && ch <= '\u06FF')
           || (ch >= '\u0750' && ch <= '\u077F')
           || (ch >= '\u08A0' && ch <= '\u08FF')
           || (ch >= '\u0870' && ch <= '\u089F');

    /// <summary>
    /// Fraction of the alphabetic characters that are Arabic script, in the range 0.0-1.0.
    /// Text with no letters scores 0.0 (claude_memory.py:145).
    /// </summary>
    public static double ArabicRatio(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;

        var letters = 0;
        var arabic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (IsArabic(ch)) arabic++;
        }

        return letters == 0 ? 0.0 : (double)arabic / letters;
    }
}
