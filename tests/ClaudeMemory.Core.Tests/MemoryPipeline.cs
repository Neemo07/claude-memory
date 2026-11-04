using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Runs the same pipeline the reference CLI runs for <c>--root &lt;fixture&gt;</c>: discover,
/// parse, drop empty sessions, select, then sort oldest-first (claude_memory.py:1946-1979).
/// </summary>
internal static class MemoryPipeline
{
    public static MemoryOptions NewOptions() => new()
    {
        Root = TestPaths.FixtureRoot,
        ProjectsDir = null,
        PrintStdout = true,
    };

    public static List<Session> Load(MemoryOptions opts)
    {
        var parsed = LoadAll(opts);
        var chosen = SessionSelector.Select(parsed, opts, currentDirectory: opts.Root);
        return SessionOrder.OldestFirst(chosen);
    }

    /// <summary>
    /// Parsed sessions in discovery order, which is what <c>--list</c> receives before any
    /// selection or sort (claude_memory.py:1964-1968).
    /// </summary>
    public static List<Session> LoadAll(MemoryOptions opts)
    {
        var files = SessionDiscovery.Discover(opts.Root, opts.ProjectsDir);
        return files
            .Select(path => JsonlSessionParser.ParseSession(path, opts))
            .Where(session => session.Turns.Count > 0 || session.Summaries.Count > 0)
            .ToList();
    }
}
