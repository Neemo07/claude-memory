using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Renders <c>--mode full</c>, ported from claude_memory.py:1557-1641
/// (<c>build_full_memory</c>).
/// </summary>
/// <remarks>
/// The orientation sections are kept at the top and charged to the budget, then every turn is
/// emitted verbatim with tool calls summarised to one line each and tool output dropped. The
/// character count in the end note is measured before that note is appended
/// (claude_memory.py:1632).
/// </remarks>
public static class FullMemoryBuilder
{
    /// <summary>Builds the unabridged memory document for the mode <c>full</c>.</summary>
    public static string Build(IReadOnlyList<Session> sessions, MemoryOptions opts, DateTimeOffset now)
    {
        var budget = new Budget(opts.MaxChars);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lines = new List<string>();
        lines.AddRange(SummaryBuilder.RenderHeader(sessions, opts, now));
        lines.Add("");

        var facts = FileFacts.Collect(sessions);
        var uncut = new List<string>();
        uncut.AddRange(SummaryBuilder.RenderSessions(sessions, opts));
        uncut.AddRange(SummaryBuilder.RenderSummaries(sessions, opts));
        lines.AddRange(uncut);
        budget.ChargeUncut(lines);

        // Orientation first, still capped, so the head of the file stays cheap to read.
        var sections = new List<List<string>>
        {
            SummaryBuilder.RenderClaims("Decisions taken", "What was settled and why.",
                sessions, Heuristics.Decision, budget, opts, opts.MaxItems, seen),
            SummaryBuilder.RenderClaims("Preferences and conventions", "Style and approach choices.",
                sessions, Heuristics.Preference, budget, opts, opts.MaxItems, seen),
            SummaryBuilder.RenderClaims("Constraints and must-nots", "Rules that later work has to respect.",
                sessions, Heuristics.Fact, budget, opts, opts.MaxItems, seen),
            SummaryBuilder.RenderFiles(facts, budget, opts, sessions),
            SummaryBuilder.RenderNextSteps(sessions, budget, opts, seen),
        };
        foreach (var section in sections)
        {
            if (section.Count > 0) lines.AddRange(section);
        }

        var turns = sessions.Sum(session => session.Turns.Count);
        var words = 0;
        var conversation = new List<string>
        {
            $"## {BilingualTitles.For("Full conversation (nothing summarised away)", sessions, opts.Lang)}",
            "",
            $"_{turns} turn(s) across {sessions.Count} session(s), verbatim. "
            + "Tool calls are listed one line each; tool output is omitted "
            + "because it is mostly file dumps._",
            "",
        };

        foreach (var session in sessions)
        {
            var span = session.Start is { } start ? $" - {start:yyyy-MM-dd HH:mm}" : "";
            conversation.Add($"### {SummaryBuilder.SessionWhere(session, opts)}{span}");
            conversation.Add("");
            if (session.Turns.Count == 0)
            {
                conversation.Add("_no turns in this session_");
                conversation.Add("");
                continue;
            }

            foreach (var turn in session.Turns)
            {
                var when = turn.Timestamp is { } timestamp ? $" - {timestamp:HH:mm}" : "";
                var tag = turn.Sidechain ? " _(subagent)_" : "";
                conversation.Add($"**{turn.Role}**{when}{tag}");
                conversation.Add("");

                var body = turn.Text.Trim();
                if (body.Length > 0)
                {
                    words += CountWords(body);
                    conversation.Add(body);
                    conversation.Add("");
                }

                foreach (var call in turn.Tools) conversation.Add($"- `{call.Name}` {call.Brief}");
                foreach (var error in turn.Errors) conversation.Add($"- **error** {error}");

                if (body.Length == 0 && turn.Tools.Count == 0 && turn.Errors.Count == 0)
                {
                    conversation.Add("_(no text)_");
                    conversation.Add("");
                }
            }
        }

        lines.AddRange(conversation);
        lines.Add("---");
        lines.Add("");
        var fullText = string.Join("\n", lines);
        lines.Add($"_End of full context: {words} words of assistant+user text, "
                  + $"{fullText.Length} chars total (~{TokenEstimator.Estimate(fullText)} tokens)._");
        lines.Add("");

        var text = string.Join("\n", lines).TrimEnd() + "\n";
        if (opts.Format == "claude-md")
        {
            text = $"{BuildInfo.BeginMarker}\n{text}\n{BuildInfo.EndMarker}\n";
        }

        return text;
    }

    /// <summary>Counts whitespace-separated tokens, matching Python's <c>len(body.split())</c>.</summary>
    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
