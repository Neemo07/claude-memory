using System.Text.RegularExpressions;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Renders <c>--mode memory</c>, ported from claude_memory.py:1061-1554
/// (<c>session_where</c>, <c>render_*</c>, <c>build_memory</c>).
/// </summary>
/// <remarks>
/// Section order and the shared <c>seen</c> set are load-bearing: the reference renders the
/// intent, recent-exchange, claims, files, errors, next-steps and identifiers sections in this
/// exact order so a line is claimed by the first section that wants it. <c>render_prefs</c> is
/// dead reference code and is intentionally not ported.
/// </remarks>
public static class SummaryBuilder
{
    /// <summary>Sessions table row cap (claude_memory.py:1117).</summary>
    public const int SessionsMaxRows = 25;

    private const string SharedLink = "shared-link";

    /// <summary>Builds the rolling memory document for the mode <c>memory</c>.</summary>
    public static string Build(IReadOnlyList<Session> sessions, MemoryOptions opts, DateTimeOffset now)
    {
        var budget = new Budget(opts.MaxChars);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Header, table and summaries are never trimmed, so charge them to the budget before
        // the trimmed sections render (claude_memory.py:1512).
        var lines = new List<string>();
        lines.AddRange(RenderHeader(sessions, opts, now));
        lines.Add("");
        lines.AddRange(RenderSessions(sessions, opts));
        lines.AddRange(RenderSummaries(sessions, opts));
        budget.ChargeUncut(lines);

        var facts = FileFacts.Collect(sessions);
        var sections = new List<List<string>>
        {
            RenderIntent(sessions, budget, opts),
            RenderRecentExchange(sessions, budget, opts),
            RenderClaims("Decisions taken", "What was settled and why (newest wins).",
                sessions, Heuristics.Decision, budget, opts, opts.MaxItems, seen),
            // preferences run before constraints: their keywords are more specific, so the
            // broad "always/never" patterns cannot swallow every convention (claude_memory.py:1529)
            RenderClaims("Preferences and conventions", "Style and approach choices.",
                sessions, Heuristics.Preference, budget, opts, opts.MaxItems, seen),
            RenderClaims("Constraints and must-nots", "Rules that later work has to respect.",
                sessions, Heuristics.Fact, budget, opts, opts.MaxItems, seen),
            RenderFiles(facts, budget, opts, sessions),
            RenderErrors(sessions, budget, opts, seen),
            RenderNextSteps(sessions, budget, opts, seen),
            RenderIdentifiers(sessions, budget, opts, facts.Opened()),
        };

        foreach (var section in sections)
        {
            if (section.Count > 0) lines.AddRange(section);
        }

        if (budget.Trimmed > 0)
        {
            var soFar = string.Join("\n", lines);
            lines.Add("---");
            lines.Add("");
            lines.Add($"_Trimmed {budget.Trimmed} lower-priority line(s) to stay inside "
                      + $"--max-chars {opts.MaxChars} (~{TokenEstimator.Estimate(soFar)} tokens). "
                      + "Raise --max-chars, or re-run with --mode transcript for full detail._");
            lines.Add("");
        }

        var text = string.Join("\n", lines).TrimEnd() + "\n";
        if (opts.Format == "claude-md")
        {
            text = $"{BuildInfo.BeginMarker}\n{text}\n{BuildInfo.EndMarker}\n";
        }

        return text;
    }

    /// <summary>Where a session happened, phrased for humans (claude_memory.py:1061).</summary>
    public static string SessionWhere(Session session, MemoryOptions opts)
    {
        if (session.Cwd.Length > 0) return TextUtil.FmtCwd(session.Cwd, opts.Home);
        if (session.Branch == SharedLink) return "(claude.ai shared chat)";
        return "(unknown cwd)";
    }

    internal static List<string> RenderHeader(IReadOnlyList<Session> sessions, MemoryOptions opts, DateTimeOffset now)
    {
        var turns = sessions.Sum(session => session.Turns.Count);
        var prompts = sessions.Sum(session => session.Prompts().Count);
        var calls = sessions.Sum(session => session.ToolCalls().Count);

        var stamps = sessions.Where(session => session.Start is not null)
            .Select(session => session.Start!.Value)
            .ToList();
        var span = stamps.Count > 0
            ? $"{stamps.Min():yyyy-MM-dd} \u2192 {stamps.Max():yyyy-MM-dd}"
            : "";

        var cwds = sessions.Where(session => session.Cwd.Length > 0)
            .Select(session => session.Cwd)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cwd => cwd, StringComparer.Ordinal)
            .ToList();

        var shared = sessions.Count(session => session.Branch == SharedLink);
        var local = sessions.Count - shared;
        var origin = shared > 0 && local > 0
            ? $"from {Plural(local, "local session")} and {Plural(shared, "shared claude.ai chat")}"
            : shared > 0
                ? $"from {Plural(shared, "shared claude.ai chat")}"
                : $"from {Plural(local, "Claude Code session")}";

        var lines = new List<string>
        {
            $"# {BilingualTitles.For("Project memory \u2014 rolling", sessions, opts.Lang)}",
            "",
            $"_Generated {BuildInfo.IsoNow(now)} by {BuildInfo.GeneratorName} v{BuildInfo.ReferenceVersion} {origin}"
            + (span.Length > 0 ? $", {span}" : "") + "._",
            "",
            $"- Turns: **{turns}** ({prompts} user prompts, {calls} tool calls)"
            + $" \u00b7 skipped noise records: **{sessions.Sum(session => session.Skipped)}**",
        };

        if (cwds.Count > 0)
        {
            var shown = cwds.Take(4).ToList();
            lines.Add("- Working dir(s): " + string.Join(", ", shown.Select(cwd => $"`{cwd}`"))
                      + (cwds.Count > shown.Count ? $" (+{cwds.Count - shown.Count} more)" : ""));
        }

        if (opts.SourceNote.Length > 0) lines.Add($"- Source: `{opts.SourceNote}`");

        lines.Add("");
        lines.Add("_Read this file first for orientation, then open the project files it names. "
                  + "It is a summary, not a transcript \u2014 verify anything load-bearing against the code._");
        return lines;
    }

    internal static List<string> RenderSessions(IReadOnlyList<Session> sessions, MemoryOptions opts)
    {
        var lines = new List<string>
        {
            $"## {BilingualTitles.For("Sessions", sessions, opts.Lang)}",
            "",
            "| # | when | cwd | branch | turns | tool calls | opened with |",
            "|---|------|-----|--------|-------|-----------|-------------|",
        };

        var ordered = sessions.Reverse().ToList(); // newest first
        var shown = ordered.Take(SessionsMaxRows).ToList();
        var baseIndex = sessions.Count - shown.Count;
        for (var offset = 0; offset < shown.Count; offset++)
        {
            var session = shown[offset];
            var when = session.Start is { } start ? start.ToString("yyyy-MM-dd HH:mm") : "?";
            var index = baseIndex + shown.Count - offset;
            var branch = session.Branch.Length > 0 ? session.Branch : "-";
            lines.Add($"| {index} | {when} | `{SessionWhere(session, opts)}` | {branch} | "
                      + $"{session.Turns.Count} | {session.ToolCalls().Count} | {session.Label()} |");
        }

        if (baseIndex > 0) lines.Add($"| \u2026 | _{baseIndex} older session(s) not listed_ | | | | | |");
        lines.Add("");
        return lines;
    }

    internal static List<string> RenderSummaries(IReadOnlyList<Session> sessions, MemoryOptions opts)
    {
        var items = sessions.Where(session => session.Summaries.Count > 0).ToList();
        if (items.Count == 0) return new List<string>();

        var lines = new List<string> { "## Compactor summaries", "" };
        foreach (var session in items)
        {
            foreach (var summary in Last(session.Summaries, 3))
            {
                lines.Add($"- _({SessionWhere(session, opts)})_ {TextUtil.OneLine(summary, 300)}");
            }
        }

        lines.Add("");
        return lines;
    }

    private static List<string> RenderIntent(IReadOnlyList<Session> sessions, Budget budget, MemoryOptions opts)
    {
        var lines = new List<string>();
        foreach (var session in sessions)
        {
            var prompts = session.Prompts();
            if (prompts.Count == 0) continue;

            var when = session.Start is { } start ? start.ToString("yyyy-MM-dd HH:mm") : "session";
            lines.Add($"### {when} \u00b7 `{SessionWhere(session, opts)}`");
            lines.Add("");

            // keep the NEWEST prompts: on a long chat the first ones are the oldest context
            var cap = Math.Max(1, opts.MaxPromptsPerSession);
            var chosen = Last(prompts, cap);
            var omitted = prompts.Count - chosen.Count;
            if (omitted > 0)
            {
                lines.Add($"> _({omitted} earlier prompt(s) omitted; newest {cap} shown)_");
                lines.Add(">");
            }

            foreach (var prompt in chosen)
            {
                lines.Add($"> {TextUtil.OneLine(prompt, opts.MaxLine)}");
                lines.Add(">");
            }

            lines.Add("");
        }

        var rendered = new List<string>
        {
            $"## {BilingualTitles.For("What was asked (verbatim user prompts)", sessions, opts.Lang)}",
            "",
        };
        rendered.AddRange(budget.Take(lines.Where(line => line.Length > 0), bullet: ""));
        return rendered;
    }

    internal static List<string> RenderClaims(string title, string hint, IReadOnlyList<Session> sessions,
        Regex regex, Budget budget, MemoryOptions opts, int limit, HashSet<string> seen)
    {
        var candidates = Claims(sessions, regex, opts);
        var fresh = new List<string>();
        foreach (var line in candidates)
        {
            var key = TextUtil.NormKey(line);
            if (seen.Contains(key)) continue;
            seen.Add(key);
            fresh.Add(line);
        }

        var (kept, _) = TextUtil.DedupeRecent(fresh, limit);
        if (kept.Count == 0) return new List<string>();

        var lines = new List<string>
        {
            $"## {BilingualTitles.For(title, sessions, opts.Lang)}",
            "",
            $"_{hint}_",
            "",
        };
        lines.AddRange(budget.Take(kept));
        lines.Add("");
        return lines;
    }

    /// <summary>Claim lines matching a heuristic, skipping prompts and low-value lines (claude_memory.py:1165).</summary>
    private static List<string> Claims(IReadOnlyList<Session> sessions, Regex regex, MemoryOptions opts)
    {
        var found = new List<string>();
        foreach (var session in sessions)
        {
            var promptKeys = new HashSet<string>(session.Prompts().Select(TextUtil.NormKey), StringComparer.Ordinal);
            foreach (var turn in session.Turns)
            {
                if (turn.Role != TurnRoles.Assistant || turn.Text.Length == 0) continue;
                foreach (var line in TextUtil.Sentences(turn.Text, opts.MaxLine))
                {
                    if (promptKeys.Contains(TextUtil.NormKey(line))) continue;
                    if (Heuristics.LowValue.IsMatch(line) || Heuristics.HeaderLine.IsMatch(line)) continue;
                    if (regex.IsMatch(line)) found.Add(line);
                }
            }
        }

        return found;
    }

    internal static List<string> RenderFiles(FileFacts facts, Budget budget, MemoryOptions opts,
        IReadOnlyList<Session> sessions)
    {
        var lines = new List<string>();

        if (facts.Edits.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Files changed", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(
                Top(facts.Edits, opts.MaxPaths).Select(entry => $"`{entry.Key}` \u00d7{entry.Value}")));
            lines.Add("");
        }

        if (facts.Reads.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Files read / searched (top)", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(
                Top(facts.Reads, opts.MaxPaths).Select(entry => $"`{entry.Key}` \u00d7{entry.Value}")));
            lines.Add("");
        }

        if (facts.Commands.Count > 0)
        {
            var (kept, _) = TextUtil.Dedupe(facts.Commands, opts.MaxCmds);
            lines.Add($"## {BilingualTitles.For("Commands run", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(kept));
            lines.Add("");
        }

        if (facts.Agents.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Subagents used", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(
                RankByCount(facts.Agents, 10).Select(entry => $"`{entry.Key}` \u00d7{entry.Value}")));
            lines.Add("");
        }

        if (facts.Unknown.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Other tools used", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(
                RankByCount(facts.Unknown, 12).Select(entry => $"`{entry.Key}` \u00d7{entry.Value}")));
        }

        return lines;
    }

    private static List<string> RenderErrors(IReadOnlyList<Session> sessions, Budget budget,
        MemoryOptions opts, HashSet<string> seen)
    {
        // checklist bullets belong in the plan section; fuzzy problem words in a plan item
        // ("diff the offsets", "report the mismatch") are not real problems (claude_memory.py:1346)
        var bulletKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            foreach (var turn in session.Turns)
            {
                if (turn.Role != TurnRoles.Assistant || turn.Text.Length == 0) continue;
                foreach (var line in TextUtil.SplitLines(turn.Text))
                {
                    if (TextUtil.IsBullet(line))
                    {
                        bulletKeys.Add(TextUtil.NormKey(TextUtil.OneLine(TextUtil.StripBullet(line), opts.MaxLine)));
                    }
                }
            }
        }

        var items = new List<string>();
        foreach (var session in sessions)
        {
            foreach (var error in session.ErrorLines()) items.Add(TextUtil.OneLine(error, opts.MaxLine));
            foreach (var turn in session.Turns)
            {
                if (turn.Role != TurnRoles.Assistant || turn.Text.Length == 0) continue;
                foreach (var line in TextUtil.Sentences(turn.Text, opts.MaxLine))
                {
                    if (bulletKeys.Contains(TextUtil.NormKey(line))) continue;
                    if (Heuristics.Problem.IsMatch(line) && !Heuristics.LowValue.IsMatch(line)) items.Add(line);
                }
            }
        }

        items = items.Where(line => !Claim(line, seen)).ToList();
        var (kept, _) = TextUtil.DedupeRecent(items, opts.MaxItems);
        if (kept.Count == 0) return new List<string>();

        var lines = new List<string>
        {
            $"## {BilingualTitles.For("Problems, failures and root causes seen", sessions, opts.Lang)}",
            "",
            "_Re-check these before repeating the same work \u2014 they may already be fixed._",
            "",
        };
        lines.AddRange(budget.Take(kept));
        lines.Add("");
        return lines;
    }

    internal static List<string> RenderNextSteps(IReadOnlyList<Session> sessions, Budget budget,
        MemoryOptions opts, HashSet<string> seen)
    {
        var todos = new List<string>();
        var plans = new List<string>();
        foreach (var session in sessions)
        {
            foreach (var turn in session.Turns)
            {
                if (turn.Role != TurnRoles.Assistant || turn.Text.Length == 0) continue;

                var rawLines = TextUtil.SplitLines(turn.Text)
                    .Where(line => line.Trim().Length > 0)
                    .Select(TextUtil.Rstrip)
                    .ToList();
                var isList = rawLines.Count(TextUtil.IsBullet) >= 2;

                foreach (var line in TextUtil.Sentences(turn.Text, opts.MaxLine))
                {
                    if (Heuristics.Open.IsMatch(line) && !line.EndsWith("?", StringComparison.Ordinal))
                    {
                        if (Heuristics.HeaderLine.IsMatch(line) || Heuristics.LowValue.IsMatch(line)) continue;
                        todos.Add(line);
                    }
                }

                if (isList)
                {
                    foreach (var line in rawLines)
                    {
                        if (TextUtil.IsBullet(line))
                            plans.Add(TextUtil.OneLine(TextUtil.StripBullet(line), opts.MaxLine));
                    }
                }
            }
        }

        todos = todos.Where(line => !Claim(line, seen)).ToList();
        var (keptTodos, _) = TextUtil.DedupeRecent(todos, opts.MaxItems / 2);

        // newest plan bullets first; never repeat a bullet already shown as a TODO
        var planSeen = new HashSet<string>(keptTodos.Select(TextUtil.NormKey), StringComparer.Ordinal);
        var freshPlans = plans.Where(line => !Claim(line, planSeen)).ToList();
        var (keptPlans, _) = TextUtil.DedupeRecent(Enumerable.Reverse(freshPlans).ToList(), opts.MaxItems);

        var lines = new List<string>();
        if (keptTodos.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Open items / TODOs", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(keptTodos));
            lines.Add("");
        }

        if (keptPlans.Count > 0)
        {
            lines.Add($"## {BilingualTitles.For("Most recent plan or checklist", sessions, opts.Lang)}");
            lines.Add("");
            lines.AddRange(budget.Take(keptPlans));
            lines.Add("");
        }

        return lines;
    }

    private static List<string> RenderIdentifiers(IReadOnlyList<Session> sessions, Budget budget,
        MemoryOptions opts, HashSet<string> skipPaths)
    {
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);
        var hexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            foreach (var turn in session.Turns)
            {
                if (turn.Text.Length == 0) continue;

                foreach (Match match in Heuristics.Path.Matches(turn.Text))
                {
                    var clean = match.Value.Trim().Trim('.', ',', ';', ')', '"', '\'');
                    if (!AlreadyListed(clean, skipPaths)) Bump(paths, clean);
                }

                foreach (Match match in Heuristics.Hex.Matches(turn.Text)) Bump(hexes, match.Value);
                foreach (Match match in Heuristics.Version.Matches(turn.Text)) Bump(versions, match.Value);
                foreach (Match match in Heuristics.Size.Matches(turn.Text)) Bump(sizes, TextUtil.OneLine(match.Value, 40));

                if (opts.IncludeAddresses)
                {
                    foreach (Match match in Heuristics.Address.Matches(turn.Text))
                    {
                        if (!Heuristics.IsHexLiteral(match.Value)) Bump(hexes, "0x" + match.Value);
                    }
                }
            }
        }

        var blocks = new List<(string Name, List<KeyValuePair<string, int>> Items)>();
        if (paths.Count > 0) blocks.Add(("Paths touched", Top(paths, opts.MaxPaths)));
        if (hexes.Count > 0) blocks.Add(("Hex / addresses", Top(hexes, opts.MaxPaths)));
        if (versions.Count > 0) blocks.Add(("Versions", Top(versions, Math.Min(12, opts.MaxPaths))));
        if (sizes.Count > 0) blocks.Add(("Measured values", Top(sizes, Math.Min(12, opts.MaxPaths))));
        if (blocks.Count == 0) return new List<string>();

        var lines = new List<string>
        {
            $"## {BilingualTitles.For("Concrete identifiers (paths, values, versions)", sessions, opts.Lang)}",
            "",
        };
        foreach (var (name, items) in blocks)
        {
            if (items.Count == 0 || budget.Left < 200) continue;
            budget.Line($"**{name}**");
            lines.Add($"**{name}**");
            lines.Add("");
            lines.AddRange(budget.Take(items.Select(entry => $"`{entry.Key}` \u00d7{entry.Value}")));
            lines.Add("");
        }

        return lines;
    }

    private static List<string> RenderRecentExchange(IReadOnlyList<Session> sessions, Budget budget, MemoryOptions opts)
    {
        var cap = Math.Max(400, opts.MaxLine * 6);
        var picked = new List<(string Place, string Head, string Body)>();

        foreach (var session in sessions)
        {
            var userIndexes = new List<int>();
            for (var index = 0; index < session.Turns.Count; index++)
            {
                if (session.Turns[index].Role == TurnRoles.User && session.Turns[index].Text.Trim().Length > 0)
                    userIndexes.Add(index);
            }

            List<Turn> tailTurns;
            if (userIndexes.Count > 0)
            {
                var start = userIndexes[^1];
                var tail = new List<Turn> { session.Turns[start] };
                for (var index = start + 1; index < session.Turns.Count; index++)
                {
                    if (session.Turns[index].Role == TurnRoles.Assistant && session.Turns[index].Text.Trim().Length > 0)
                        tail.Add(session.Turns[index]);
                }

                tailTurns = Last(tail, 5);
            }
            else
            {
                var replies = session.Turns
                    .Where(turn => turn.Role == TurnRoles.Assistant && turn.Text.Trim().Length > 0)
                    .ToList();
                tailTurns = Last(replies, 1);
            }

            foreach (var turn in tailTurns)
            {
                var body = turn.Text.Trim();
                if (body.Length == 0) continue;
                var (clipped, trimmed) = TextUtil.ClipMiddle(body, cap);
                var who = turn.Role == TurnRoles.User ? "user" : "assistant";
                var label = who == "user" ? "last question" : "reply since then";
                var note = trimmed ? $" _({body.Length} chars, middle omitted)_" : "";
                picked.Add((SessionWhere(session, opts), $"**{who}** \u2014 {label}{note}", clipped));
            }
        }

        if (picked.Count == 0) return new List<string>();

        var lines = new List<string>
        {
            $"## {BilingualTitles.For("Most recent exchange", sessions, opts.Lang)}",
            "",
            "_The last question and everything answered since it, kept verbatim so the summary "
            + "never stops short of what was actually concluded._",
            "",
        };
        foreach (var (place, head, body) in picked)
        {
            lines.AddRange(budget.Take(new[] { $"### {place}", "", head, "", body, "" }, bullet: ""));
        }

        return lines;
    }

    /// <summary>Registers a line as already rendered; returns true when it is a duplicate.</summary>
    private static bool Claim(string line, HashSet<string> seen)
    {
        var key = TextUtil.NormKey(line);
        if (seen.Contains(key)) return true;
        seen.Add(key);
        return false;
    }

    /// <summary><c>sorted(..., key=(-count, key))[:n]</c> (claude_memory.py:1246).</summary>
    private static List<KeyValuePair<string, int>> Top(Dictionary<string, int> store, int count)
        => store.OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(count)
            .ToList();

    /// <summary><c>sorted(..., key=-count)[:n]</c>: ties keep insertion order (claude_memory.py:1333).</summary>
    private static List<KeyValuePair<string, int>> RankByCount(Dictionary<string, int> store, int count)
        => store.OrderByDescending(entry => entry.Value).Take(count).ToList();

    private static bool AlreadyListed(string path, HashSet<string> skipPaths)
    {
        var normalised = path.Replace('\\', '/').ToLowerInvariant();
        foreach (var opened in skipPaths)
        {
            if (opened.EndsWith(normalised, StringComparison.Ordinal)
                || normalised.EndsWith(opened, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static void Bump(Dictionary<string, int> store, string key)
        => store[key] = store.TryGetValue(key, out var count) ? count + 1 : 1;

    private static List<T> Last<T>(IReadOnlyList<T> items, int count)
        => items.Skip(Math.Max(0, items.Count - count)).ToList();

    private static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";
}
