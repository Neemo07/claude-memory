namespace ClaudeMemory.Core.Text;

/// <summary>
/// Script-weighted token estimate, ported from claude_memory.py:245-293.
/// </summary>
/// <remarks>
/// This is an estimate, not a tokenizer. One <c>// 4</c> ratio is calibrated for English and
/// under-reports Arabic by roughly 2.5x, so each script is counted at its own rate.
/// </remarks>
public static class TokenEstimator
{
    private const double CharsPerTokenLatin = 4.0;
    private const double CharsPerTokenArabic = 1.6;
    private const double CharsPerTokenCyrillic = 2.2;
    private const double CharsPerTokenCjk = 1.0;

    /// <summary>Rough token count for the text (rounded, never negative).</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double latin = 0, arabic = 0, cyrillic = 0, cjk = 0, other = 0;
        foreach (var ch in text)
        {
            if (ch <= '\u007f') latin++;
            else if (ArabicText.IsArabic(ch)) arabic++;
            else if (ch >= '\u0400' && ch <= '\u04ff') cyrillic++;
            else if ((ch >= '\u3040' && ch <= '\u30ff')
                     || (ch >= '\u3400' && ch <= '\u9fff')
                     || (ch >= '\uf900' && ch <= '\ufaff')) cjk++;
            else other++;
        }

        var total = latin / CharsPerTokenLatin
                    + arabic / CharsPerTokenArabic
                    + cyrillic / CharsPerTokenCyrillic
                    + cjk / CharsPerTokenCjk
                    + other / CharsPerTokenLatin;
        return (int)(total + 0.5);
    }
}
