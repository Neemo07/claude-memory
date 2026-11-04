using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Renders <c>--mode transcript</c>, ported from claude_memory.py:1644-1675
/// (<c>build_transcript</c>).
/// </summary>
/// <remarks>
/// Every turn is emitted with its raw text, tool calls as one line each and errors as bullets.
/// The reference trusts <c>s.end</c> to be set whenever <c>s.start</c> is; when it is not, the
/// start is reused so a truncated file cannot crash the build (see IMPLEMENTATION_PLAN.md section 2.1).
/// </remarks>
public static class TranscriptBuilder
{
    /// <summary>Builds the per-session transcript export for the mode <c>transcript</c>.</summary>
    public static string Build(IReadOnlyList<Session> sessions, MemoryOptions opts, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            "# Claude Code transcript export",
            "",
            $"_{sessions.Count} session(s), generated {BuildInfo.IsoNow(now)}_",
            "",
        };

        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            lines.Add("---");
            lines.Add("");
            lines.Add($"## Session {index + 1} \u00b7 `{System.IO.Path.GetFileName(session.Path)}`");
            lines.Add("");

            var meta = new List<string>
            {
                $"- cwd: `{(session.Cwd.Length > 0 ? session.Cwd : SummaryBuilder.SessionWhere(session, opts))}`",
            };
            if (session.Branch.Length > 0) meta.Add($"- branch: `{session.Branch}`");
            if (session.Model.Length > 0) meta.Add($"- model: `{session.Model}`");
            if (session.Start is { } start)
            {
                var end = session.End ?? start;
                meta.Add($"- span: {start:yyyy-MM-dd HH:mm} \u2192 {end:HH:mm} "
                         + $"({TextUtil.FmtDur(session.Duration)})");
            }

            meta.Add($"- turns: {session.Turns.Count} \u00b7 tool calls: {session.ToolCalls().Count} "
                     + $"\u00b7 records skipped: {session.Skipped}");
            lines.AddRange(meta);
            lines.Add("");

            if (session.TruncatedFile)
            {
                lines.Add("> _Only the last part of this file was read (--max-file-bytes)._");
                lines.Add("");
            }

            foreach (var turn in session.Turns)
            {
                if (turn.Text.Length > 0)
                {
                    lines.Add($"### {turn.Role}" + (turn.Sidechain ? " _(subagent)_" : "")
                              + (turn.Timestamp is { } timestamp ? $" \u00b7 {timestamp:HH:mm}" : ""));
                    lines.Add("");
                    lines.Add(turn.Text.Trim());
                    lines.Add("");
                }

                foreach (var call in turn.Tools) lines.Add($"- `{call.Name}` {call.Brief}");
                foreach (var error in turn.Errors) lines.Add($"- **error** {error}");
            }
        }

        return string.Join("\n", lines).TrimEnd() + "\n";
    }
}
