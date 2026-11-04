using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Text;

/// <summary>
/// Timestamp parsing ported from claude_memory.py:355-387 (<c>parse_ts</c>, <c>parse_since</c>).
/// </summary>
/// <remarks>
/// The reference calls <c>datetime.fromisoformat</c>, which accepts more spellings than the
/// format list below (week dates, sub-minute times, basic ISO). The formats here cover every
/// spelling the app and Claude transcripts produce; the tested vectors are in
/// <c>TimeWindowTests</c>.
/// </remarks>
public static class TimeWindow
{
    private static readonly Regex RelativeSince = new(
        @"^(\d+)\s*([mhdw])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mmzzz",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
        "yyyy-MM",
        "yyyyMMddTHHmmss.FFFFFFFzzz",
        "yyyyMMddTHHmmss.FFFFFFF",
        "yyyyMMddTHHmmsszzz",
        "yyyyMMddTHHmmss",
        "yyyyMMdd",
    };

    private static readonly string[] LocalFormats =
    {
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "MM/dd/yyyy",
        "dd/MM/yyyy",
    };

    /// <summary>
    /// Parses an ISO-8601 transcript timestamp. A missing or unusable value returns null; a
    /// value without an offset is treated as UTC (claude_memory.py:355).
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var text = value.Trim().Replace("Z", "+00:00");
        foreach (var candidate in Candidates(text))
        {
            if (DateTimeOffset.TryParseExact(
                    candidate,
                    IsoFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Accepts <c>30m</c>, <c>2h</c>, <c>7d</c>, <c>1w</c> (relative to <paramref name="now"/>),
    /// <c>YYYY-MM-DD</c>, an ISO timestamp, or <c>MM/dd/yyyy</c>.
    /// Throws <see cref="FormatException"/> with the reference message when nothing matches
    /// (claude_memory.py:368).
    /// </summary>
    public static DateTimeOffset ParseSince(string? text, DateTimeOffset now)
    {
        var value = (text ?? "").Trim();

        var relative = RelativeSince.Match(value);
        if (relative.Success)
        {
            var count = double.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture);
            return relative.Groups[2].Value.ToLowerInvariant() switch
            {
                "m" => now.AddMinutes(-count),
                "h" => now.AddHours(-count),
                "d" => now.AddDays(-count),
                _ => now.AddDays(-7 * count),
            };
        }

        if (ParseTimestamp(value) is { } timestamp) return timestamp;

        foreach (var format in LocalFormats)
        {
            if (DateTimeOffset.TryParseExact(
                    value,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        throw new FormatException($"cannot parse --since value: {PythonRepr(value)}");
    }

    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        yield return Shorten(text, 19);

        // claude_memory.py:359: the third attempt swaps the first space for a 'T'.
        var space = text.IndexOf(' ');
        var swapped = space < 0 ? text : string.Concat(text.AsSpan(0, space), "T", text.AsSpan(space + 1));
        yield return Shorten(swapped, 19);
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Formats a string the way Python's <c>repr()</c> formats the common cases.</summary>
    private static string PythonRepr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var body = value.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote);
        return quote + body + quote;
    }
}
