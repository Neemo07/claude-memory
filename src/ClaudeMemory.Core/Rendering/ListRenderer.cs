using System.Globalization;
using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Renders the <c>--list</c> table, ported from claude_memory.py:1734-1753
/// (<c>render_list</c>).
/// </summary>
/// <remarks>
/// The reference takes the selection in discovery order, not oldest-first, and reads the file
/// size from disk. Python formats sizes with <c>format(x, ".0f")</c>, which rounds halves to
/// even; <see cref="Fixed"/> reproduces that instead of .NET's half-away-from-zero default.
/// </remarks>
public static class ListRenderer
{
    /// <summary>Shown when no transcript was found (claude_memory.py:1735).</summary>
    public const string EmptyMessage =
        "No Claude Code transcripts found.\n"
        + "Expected .jsonl files under ~/.claude/projects/<slug>/.\n"
        + "Start a Claude Code session in a project directory first.\n";

    public static string Build(IReadOnlyList<Session> sessions, MemoryOptions opts)
    {
        if (sessions.Count == 0) return EmptyMessage;

        var rows = new List<string>
        {
            "| # | file | cwd | branch | start | turns | tools | size |",
            "|---|------|-----|--------|-------|-------|-------|------|",
        };

        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var size = FileSize(session.Path);
            var when = session.Start is { } start ? start.ToString("yyyy-MM-dd HH:mm") : "?";
            var branch = session.Branch.Length > 0 ? session.Branch : "-";

            rows.Add($"| {index + 1} | `{System.IO.Path.GetFileName(session.Path)}` | "
                     + $"`{SummaryBuilder.SessionWhere(session, opts)}` | {branch} | "
                     + $"{when} | {session.Turns.Count} | {session.ToolCalls().Count} | "
                     + $"{Fixed(size / 1024.0, 0)} KB |");
        }

        var total = sessions.Where(session => File.Exists(session.Path))
            .Sum(session => FileSize(session.Path));
        rows.Add("");
        rows.Add($"_{sessions.Count} session file(s), {Fixed(total / 1024.0 / 1024.0, 2)} MB total._");
        return string.Join("\n", rows) + "\n";
    }

    /// <summary>File length, or 0 when it cannot be read (the reference's <c>except OSError</c>).</summary>
    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Python <c>format(value, ".Nf")</c>: round half to even, then fixed-point.</summary>
    private static string Fixed(double value, int digits)
        => Math.Round(value, digits, MidpointRounding.ToEven)
            .ToString("F" + digits, CultureInfo.InvariantCulture);
}