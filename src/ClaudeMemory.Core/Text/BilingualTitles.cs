using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Text;

/// <summary>
/// Bilingual section headers, ported from claude_memory.py:158-204
/// (<c>BILINGUAL_TITLES</c>, <c>bilingual_title</c>).
/// </summary>
/// <remarks>
/// Arabic is written as <c>\uXXXX</c> escapes so the source stays ASCII-only; the values are
/// the reference strings exactly. Note that "Compactor summaries" has no Arabic entry and
/// "Stated preferences and conventions" is only used by dead reference code
/// (<c>render_prefs</c>), so it is intentionally absent.
/// </remarks>
public static class BilingualTitles
{
    private static readonly (string English, string Arabic)[] Titles =
    {
        ("Sessions", "\u0627\u0644\u062c\u0644\u0633\u0627\u062a"),
        ("Compactor summaries", "\u0645\u0644\u062e\u0635\u0627\u062a \u0627\u0644\u062c\u0644\u0633\u0627\u062a"),
        ("Most recent exchange", "\u0622\u062e\u0631 \u062a\u0628\u0627\u062f\u0644"),
        ("What was asked (verbatim user prompts)", "\u0645\u0627 \u062a\u0645 \u0637\u0644\u0628\u0647 ( prompts \u0627\u0644\u0645\u0633\u062a\u062e\u062f\u0645 \u062d\u0631\u0641\u064a\u0627)"),
        ("Decisions taken", "\u0627\u0644\u0642\u0631\u0627\u0631\u0627\u062a \u0627\u0644\u0645\u062a\u062e\u0630\u0629"),
        ("Preferences and conventions", "\u0627\u0644\u062a\u0641\u0636\u064a\u0644\u0627\u062a \u0648\u0627\u0644\u0627\u062a\u0641\u0627\u0642\u064a\u0627\u062a"),
        ("Constraints and must-nots", "\u0627\u0644\u0642\u064a\u0648\u062f \u0648\u0627\u0644\u0645\u062d\u0638\u0648\u0631\u0627\u062a"),
        ("Files changed", "\u0627\u0644\u0645\u0644\u0641\u0627\u062a \u0627\u0644\u0645\u0639\u062f\u0644\u0629"),
        ("Files read / searched (top)", "\u0627\u0644\u0645\u0644\u0641\u0627\u062a \u0627\u0644\u0645\u0642\u0631\u0648\u0621\u0629 / \u0628\u062d\u062b\u062a (\u0627\u0644\u0623\u0639\u0644\u0649)"),
        ("Commands run", "\u0627\u0644\u0623\u0648\u0627\u0645\u0631 \u0627\u0644\u0645\u0646\u0641\u0630\u0629"),
        ("Subagents used", "\u0627\u0644\u0648\u0643\u0644\u0627\u0621 \u0627\u0644\u0641\u0631\u0639\u064a\u0648\u0646 \u0627\u0644\u0645\u0633\u062a\u062e\u062f\u0645\u0648\u0646"),
        ("Other tools used", "\u0623\u062f\u0648\u0627\u062a \u0623\u062e\u0631\u0649 \u0645\u0633\u062a\u062e\u062f\u0645\u0629"),
        ("Problems, failures and root causes seen", "\u0627\u0644\u0645\u0634\u0627\u0643\u0644 \u0648\u0627\u0644\u0623\u0639\u0637\u0627\u0644 \u0648\u0623\u0633\u0628\u0627\u0628\u0647\u0627 \u0627\u0644\u062c\u0630\u0631\u064a\u0629"),
        ("Open items / TODOs", "\u0627\u0644\u0639\u0646\u0627\u0635\u0631 \u0627\u0644\u0645\u0641\u062a\u0648\u062d\u0629 / \u0627\u0644\u0645\u0647\u0627\u0645"),
        ("Most recent plan or checklist", "\u0623\u062d\u062f\u062b \u062e\u0637\u0629 \u0623\u0648 \u0642\u0627\u0626\u0645\u0629 \u062a\u062d\u0642\u0642"),
        ("Concrete identifiers (paths, values, versions)", "\u0645\u0639\u0631\u0641\u0627\u062a \u0645\u0644\u0645\u0648\u0633\u0629 (\u0645\u0633\u0627\u0631\u0627\u062a\u060c \u0642\u064a\u0645\u060c \u0625\u0635\u062f\u0627\u0631\u0627\u062a)"),
        ("Full conversation (nothing summarised away)", "\u0627\u0644\u0645\u062d\u0627\u062f\u062b\u0629 \u0627\u0644\u0643\u0627\u0645\u0644\u0629 (\u0628\u062f\u0648\u0646 \u062a\u0644\u062e\u064a\u0635)"),
        ("Project memory \u2014 rolling", "\u0630\u0627\u0643\u0631\u0629 \u0627\u0644\u0645\u0634\u0631\u0648\u0639 \u2014 \u0645\u062a\u062c\u062f\u062f\u0629"),
    };

    private static readonly Dictionary<string, string> Map = Build();

    /// <summary>Arabic for an English title, or the empty string when there is no translation.</summary>
    public static string Arabic(string englishTitle) => Map.TryGetValue(englishTitle, out var arabic) ? arabic : "";

    /// <summary>
    /// Chooses a section header for the requested language. <c>auto</c> switches to bilingual
    /// when more than 15% of the session letters are Arabic (claude_memory.py:180).
    /// </summary>
    public static string For(string englishTitle, IReadOnlyList<Session>? sessions, string lang)
    {
        if (sessions is null || sessions.Count == 0) return englishTitle;

        var arabic = Arabic(englishTitle);
        if (lang == "en") return englishTitle;
        if (lang == "ar") return arabic.Length > 0 ? arabic : englishTitle;
        if (lang == "both") return arabic.Length > 0 ? $"{englishTitle} | {arabic}" : englishTitle;

        var allText = string.Join(
            " ",
            sessions.SelectMany(session => session.Turns)
                .Where(turn => turn.Text.Length > 0)
                .Select(turn => turn.Text));
        return ArabicText.ArabicRatio(allText) > 0.15 && arabic.Length > 0
            ? $"{englishTitle} | {arabic}"
            : englishTitle;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (english, arabic) in Titles) map[english] = arabic;
        return map;
    }
}
