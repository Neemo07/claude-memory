using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Applies the source, branch, time and count filters, ported from claude_memory.py:1031-1055
/// (<c>select</c>).
/// </summary>
public static class SessionSelector
{
    public static List<Session> Select(IReadOnlyList<Session> sessions, MemoryOptions opts, string currentDirectory)
    {
        var chosen = sessions.ToList();

        var needles = new List<string>();
        if (opts.Here) needles.Add(Normalise(currentDirectory));
        if (opts.Cwd.Length > 0) needles.Add(Normalise(opts.Cwd));
        if (needles.Count > 0)
        {
            chosen = chosen
                .Where(session => needles.Any(needle => Normalise($"{session.Cwd} {session.Path}").Contains(needle, StringComparison.Ordinal)))
                .ToList();
        }

        if (opts.Branch.Length > 0)
            chosen = chosen.Where(session => session.Branch.ToLowerInvariant() == opts.Branch.ToLowerInvariant()).ToList();

        if (opts.Since is { } cutoff)
        {
            chosen = chosen
                .Where(session =>
                {
                    var anchor = session.End ?? session.Start;
                    return anchor is null || anchor >= cutoff;
                })
                .ToList();
        }

        if (opts.Sessions > 0 && chosen.Count > opts.Sessions)
            chosen = chosen.GetRange(chosen.Count - opts.Sessions, opts.Sessions);

        return chosen;
    }

    private static string Normalise(string value) => value.Replace('\\', '/').ToLowerInvariant();
}
