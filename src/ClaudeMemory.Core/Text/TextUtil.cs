using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Text;

/// <summary>
/// String helpers ported from claude_memory.py (helpers section, lines 390-533, plus
/// <c>clip_middle</c> at line 1450).
/// </summary>
public static class TextUtil
{
    /// <summary>Default per-line cap used by <see cref="OneLine"/> and <see cref="Sentences"/>.</summary>
    public const int DefaultLineLimit = 400;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex BulletPrefix = new(@"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.Compiled);

    // claude_memory.py:411. The lookahead set includes Latin, backtick, quotes,
    // Arabic ranges, a digit, an em dash and a hyphen.
    private static readonly Regex SentenceBoundary = new(
        "(?<=[.!?\u06D4\u061F\u061B])\\s+(?=[A-Z`\"'\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF\\d\u2014-])",
        RegexOptions.Compiled);

    // claude_memory.py:427. ASCII-only key: Latin text collapses to letters and digits.
    private static readonly Regex DedupeNoise = new("[^a-z0-9]+", RegexOptions.Compiled);

    // claude_memory.py:235. Keeps Arabic script so Arabic lines do not all collapse to one key.
    private static readonly Regex KeyNoise = new("[^a-z0-9\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF]+", RegexOptions.Compiled);

    /// <summary>Collapses whitespace and caps the length with an ellipsis (claude_memory.py:390).</summary>
    public static string OneLine(string? text, int limit = DefaultLineLimit)
    {
        var flat = WhitespaceRun.Replace(text ?? "", " ").Trim();
        if (flat.Length <= limit) return flat;
        return flat[..(limit - 1)].TrimEnd() + "\u2026";
    }

    /// <summary>
    /// Splits a text blob into candidate claim lines: newlines separate, prose is split on
    /// sentence boundaries, checklist bullets are never split (claude_memory.py:395).
    /// </summary>
    public static List<string> Sentences(string? text, int limit = DefaultLineLimit)
    {
        var result = new List<string>();
        foreach (var raw in SplitLines(text ?? ""))
        {
            var line = raw.Trim().TrimStart('#', '>', '*', '`').Trim();
            if (line.Length == 0) continue;

            if (BulletPrefix.IsMatch(raw))
            {
                result.Add(OneLine(BulletPrefix.Replace(line, "", 1).Trim(), limit));
                continue;
            }

            foreach (var piece in SentenceBoundary.Split(line))
            {
                var candidate = piece.Trim();
                if (candidate.Length == 0) continue;
                if (candidate.Length > limit) candidate = OneLine(candidate, limit * 2);
                result.Add(OneLine(candidate, limit));
            }
        }

        return result.FindAll(s => s.Length > 0);
    }

    /// <summary>Deduplicates on an ASCII-only key, keeping the first occurrence (claude_memory.py:422).</summary>
    public static (List<string> Items, int Dropped) Dedupe(IEnumerable<string> items, int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        var dropped = 0;
        foreach (var item in items)
        {
            var key = Key(item, DedupeNoise);
            if (key.Length == 0 || !seen.Add(key))
            {
                dropped++;
                continue;
            }

            kept.Add(item);
            if (kept.Count >= limit) break;
        }

        return (kept, dropped);
    }

    /// <summary>
    /// Like <see cref="Dedupe"/> but the newest items win, so superseded decisions and
    /// errors do not report a stale state (claude_memory.py:439).
    /// </summary>
    public static (List<string> Items, int Dropped) DedupeRecent(IEnumerable<string> items, int limit)
    {
        var rows = items.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<int>();
        var dropped = 0;
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var key = Key(rows[index], DedupeNoise);
            if (key.Length == 0 || !seen.Add(key))
            {
                dropped++;
                continue;
            }

            picked.Add(index);
            if (picked.Count >= limit) break;
        }

        picked.Reverse();
        return (picked.Select(i => rows[i]).ToList(), dropped);
    }

    /// <summary>Normalised dedup key that preserves Arabic script (claude_memory.py:229).</summary>
    public static string NormKey(string? line) => Key(line ?? "", KeyNoise);

    /// <summary>
    /// Human-readable working directory: <c>~/rel</c> under the home folder, otherwise a
    /// forward-slashed path clipped to 70 characters (claude_memory.py:513).
    /// </summary>
    public static string FmtCwd(string? cwd, string? home)
    {
        if (string.IsNullOrEmpty(cwd)) return "(unknown cwd)";

        // Python's Path() normalises separators to '\' on Windows; only that is reproduced here.
        var normalised = cwd.Replace('/', '\\');
        if (!string.IsNullOrEmpty(home))
        {
            var homeNormalised = home.Replace('/', '\\');
            if (normalised.StartsWith(homeNormalised, StringComparison.OrdinalIgnoreCase))
            {
                var rest = normalised[homeNormalised.Length..];
                if (rest.Length == 0 || rest[0] == '\\')
                {
                    var relative = rest.Trim('\\');
                    if (relative.Length == 0) relative = ".";
                    return "~/" + relative.Replace('\\', '/');
                }
            }
        }

        var text = normalised.Replace('\\', '/');
        return text.Length <= 70 ? text : "\u2026" + text[^69..];
    }

    /// <summary>Durations as <c>Ns</c>, <c>Nm Ss</c> or <c>Nh Mm</c> (claude_memory.py:527).</summary>
    public static string FmtDur(double seconds)
    {
        var whole = Math.Max(0, (int)seconds);
        if (whole < 60) return $"{whole}s";
        if (whole < 3600) return $"{whole / 60}m {whole % 60}s";
        return $"{whole / 3600}h {(whole % 3600) / 60}m";
    }

    /// <summary>
    /// Shortens to <paramref name="cap"/> keeping both the start and the end, because
    /// conclusions live at the end of a reply (claude_memory.py:1450).
    /// </summary>
    public static (string Text, bool Clipped) ClipMiddle(string? text, int cap)
    {
        var value = text ?? "";
        if (value.Length <= cap) return (value, false);

        var head = (int)(cap * 0.55);
        var tail = cap - head;
        var omitted = value.Length - cap;
        return (value[..head].TrimEnd()
                + $"\n\n\u2026 [{omitted} chars omitted] \u2026\n\n"
                + value[^tail..].TrimStart(),
            true);
    }

    private static string Key(string value, Regex noise)
    {
        var key = noise.Replace(value.ToLowerInvariant(), "");
        return key.Length <= 120 ? key : key[..120];
    }

    /// <summary>
    /// Splits like Python's <c>str.splitlines()</c>: CR, LF, CRLF, vertical tab, form feed,
    /// file/group/record separators, NEL and the Unicode line/paragraph separators.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\r')
            {
                yield return text[start..index];
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                start = index + 1;
            }
            else if (current == '\n' || current == '\v' || current == '\f' || current == '\x1c'
                     || current == '\x1d' || current == '\x1e' || current == '\x85'
                     || current == '\u2028' || current == '\u2029')
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        if (start < text.Length) yield return text[start..];
    }
}
