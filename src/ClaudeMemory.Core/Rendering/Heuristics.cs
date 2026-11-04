using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Content heuristics, copied from the regexes in claude_memory.py:69-137.
/// </summary>
/// <remarks>
/// Patterns are byte-for-byte the Python ones (Arabic included) so the section membership
/// and ordering match the frozen golden files. <c>RegexOptions.IgnoreCase</c> replaces
/// Python's <c>re.I</c>.
/// </remarks>
public static class Heuristics
{
    private const RegexOptions I = RegexOptions.IgnoreCase;

    public static readonly Regex Decision = new(
        "\\b(we(?:'ll|\\s+will|\\s+decided|\\s+agreed)|i(?:'ll|\\s+will)\\s+(?:use|go with|choose|switch|"
        + "add|remove|write)|let'?s\\s+(use|go|do|start|build|fix)|going with|instead of|rather than|decisi"
        + "on|approach is|plan is|chose|chosen|opted|i(?:'d| would)\\s+recommend|\u0642\u0631\u0631\u0646"
        + "\u0627|\u0633\u0646\u0633\u062a\u062e\u062f\u0645|\u0633\u0646\u0633\u062a\u062e\u062f\u0645|"
        + "\u0633\u0646\u062e\u062a\u0627\u0631|\u0633\u0646\u0636\u064a\u0641|\u0633\u0646\u0632\u064a"
        + "\u0644|\u0633\u0646\u0643\u062a\u0628|\u0644\u0646\u0633\u062a\u062e\u062f\u0645|\u0644\u0646"
        + "\u0630\u0647\u0628|\u0644\u0646\u0628\u062f\u0623|\u0644\u0627\u0628\u0646\u0649|\u0644\u0646"
        + "\u0635\u0644\u062d|\u0628\u062f\u0644\u0627\\s+\u0645\u0646|\u0627\u0644\u0642\u0631\u0627\u0631"
        + "|\u0627\u0644\u0646\u0647\u062c|\u0627\u0644\u062e\u0637\u0629|\u0627\u062e\u062a\u0631\u0646"
        + "\u0627|\u0646\u0648\u0635\u064a|\u0633\u0646\u0647\u062a\u0645)\\b",
        RegexOptions.Compiled | I);

    public static readonly Regex Fact = new(
        "\\b(must(?: not)?|never|always|cannot|can'?t|do(?:n'?t| not)|required|requires|depends on|only i"
        + "f|note that|important|caveat|constraint|only works|is read[- ]only|has no|there is no|\u064a"
        + "\u062c\u0628|\u064a\u062c\u0628\\s+\u0644\u0627|\u0623\u0628\u062f\u0627|\u062f\u0627\u0626"
        + "\u0645\u0627|\u0644\u0627\\s+\u064a\u0645\u0643\u0646|\u0644\u0627\\s+\u062a\u0633\u062a\u0637"
        + "\u064a\u0639|\u0645\u0637\u0644\u0648\u0628|\u0644\u0627\u0632\u0645|\u0639\u0644\u0645\u0627\\s"
        + "+\u0623\u0646|\u0645\u0647\u0645|\u064a\u0639\u062a\u0645\u062f\\s+\u0639\u0644\u0649|\u0641"
        + "\u0642\u0637\\s+\u0625\u0630\u0627|\u0644\u0627\\s+\u064a\u0648\u062c\u062f|\u063a\u064a\u0631\\"
        + "s+\u0645\u0633\u0645\u0648\u062d|\u0645\u0645\u0646\u0648\u0639)\\b",
        RegexOptions.Compiled | I);

    public static readonly Regex Problem = new(
        "\\b(error|errors|exception|traceback|fail(?:ed|s|ing|ure)?|broken|breaks?|crash(?:ed|es)?|segfau"
        + "lt|regression|root cause|block(?:ed|er)|denied|refused|timed? ?out|hangs?|deadlock|leak|mismatch"
        + "|wrong|missing|\u062e\u0637\u0623|\u0641\u0634\u0644|\u062a\u0648\u0642\u0641|\u0645\u0639\u0637"
        + "\u0644|\u0645\u0634\u0643\u0644\u0629|\u0639\u0637\u0644|\u063a\u064a\u0631\\s+\u0635\u062d"
        + "\u064a\u062d|\u0645\u0641\u0642\u0648\u062f|\u0645\u0631\u0641\u0648\u0636)\\b",
        RegexOptions.Compiled | I);

    public static readonly Regex Open = new(
        "(\\?\\s*$|\\bTODO\\b|\\bnext steps?\\b|\\bremaining\\b|\\bstill need|\\bopen (?:question|item|is"
        + "sue)s?\\b|\\bfollow[- ]?up\\b|\\bblocker|\\bpending\\b|\\bunresolved\\b|\\bnot yet\\b|\u0627"
        + "\u0644\u062e\u0637\u0648\u0627\u062a\\s+\u0627\u0644\u062a\u0627\u0644\u064a\u0629|\u0627\u0644"
        + "\u0645\u062a\u0628\u0642\u064a|\u0645\u0627\\s+\u0632\u0627\u0644\\s+\u0645\u0637\u0644\u0648"
        + "\u0628|\u0645\u0641\u062a\u0648\u062d|\u0642\u064a\u062f\\s+\u0627\u0644\u0627\u0646\u062a\u0638"
        + "\u0627\u0631|\u063a\u064a\u0631\\s+\u0645\u062d\u0644\u0648\u0644|\u0644\u0645\\s+\u064a\u062a"
        + "\u0645\\s+\u0628\u0639\u062f)",
        RegexOptions.Compiled | I);

    public static readonly Regex Preference = new(
        "\\b(prefer|convention|style guide|house rule|standard is|avoid|never mix|always use|idomatic|kee"
        + "p (?:it|them) simple|YAGNI|\u0623\u0641\u0636\u0644|\u0627\u062a\u0641\u0627\u0642\u064a\u0629|"
        + "\u0645\u0639\u064a\u0627\u0631|\u0642\u0627\u0639\u062f\u0629|\u062a\u062c\u0646\u0628|\u062f"
        + "\u0627\u0626\u0645\u0627\\s+\u0627\u0633\u062a\u062e\u062f\u0645|\u0639\u0644\u0649\\s+\u0628"
        + "\u0633\u0627\u0637\u0629)\\b",
        RegexOptions.Compiled | I);

    public static readonly Regex Path = new(
        "(?<![\\w/\\\\])((?:[A-Za-z]:)?[\\\\/](?:[\\w.\\- ]+[\\\\/])*[\\w.\\-]+\\.(?:py|cpp|cxx|h|hpp|md|"
        + "rst|json|jsonl|yml|yaml|toml|ini|cfg|conf|txt|log|ts|js|tsx|jsx|rs|go|sh|ps1|bat|sql|xml|csv|zip"
        + "|dll|exe|so|elf|map|asm|s|lua|dat|bin))\\b",
        RegexOptions.Compiled | RegexOptions.None);

    public static readonly Regex Hex = new(
        "\\b0x[0-9A-Fa-f]{2,}\\b",
        RegexOptions.Compiled | RegexOptions.None);

    public static readonly Regex Version = new(
        "\\bv?\\d+\\.\\d+(?:\\.\\d+){1,3}\\b",
        RegexOptions.Compiled | RegexOptions.None);

    public static readonly Regex Size = new(
        "\\b\\d+(?:\\.\\d+)?\\s?(?:ms|us|ns|s|sec|secs|second|seconds|min|minutes|hour|hours|day|days|kb|"
        + "mb|gb|kib|mib|gib|bytes?|b|%|x|times)\\b",
        RegexOptions.Compiled | I);

    public static readonly Regex Bullet = new(
        "^\\s*(?:[-*+\u2022]|\\d+[.)])\\s+",
        RegexOptions.Compiled | RegexOptions.None);

    public static readonly Regex Address = new(
        "\\b(?:0x)?[0-9A-Fa-f]{6,8}\\b",
        RegexOptions.Compiled | RegexOptions.None);

    public static readonly Regex LowValue = new(
        "^(yes|no|nope|yeah|correct|right|sure|ok|okay|exactly|indeed|got it|understood|thanks|\u0646"
        + "\u0639\u0645|\u0644\u0627|\u0635\u062d\u064a\u062d|\u062d\u0633\u0646\u0627|\u0641\u0647\u0645"
        + "\u062a|\u0634\u0643\u0631\u0627|\u062a\u0645\u0627\u0645\u0627|\u0628\u0627\u0644\u0636\u0628"
        + "\u0637)\\b[\\s,.!:;\u2014-]",
        RegexOptions.Compiled | I);

    public static readonly Regex HeaderLine = new(
        "^[\\w\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff][\\w\u0600-\u06ff\u0750-\u077f\u08a0-\u08ff \\t/&+-"
        + "]{0,58}:$",
        RegexOptions.Compiled | RegexOptions.None);

    /// <summary>True when the whole string is a hex literal (claude_memory.py:1242).</summary>
    public static bool IsHexLiteral(string value) => Hex.Match(value).Length == value.Length && Hex.IsMatch(value);
}
