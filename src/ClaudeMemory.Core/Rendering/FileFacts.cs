using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Tool activity rolled up per file and command (claude_memory.py:1272-1308).
/// </summary>
public sealed class FileFacts
{
    /// <summary>Tool names whose argument is an edited path (claude_memory.py:1285).</summary>
    private static readonly HashSet<string> EditTools =
        new(new[] { "Edit", "MultiEdit", "Write", "NotebookEdit", "NotebookEditCell" }, StringComparer.Ordinal);

    /// <summary>Tool names whose argument is a consulted path (claude_memory.py:1286).</summary>
    private static readonly HashSet<string> ReadTools =
        new(new[] { "Read", "Glob", "Grep" }, StringComparer.Ordinal);

    public Dictionary<string, int> Edits { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = new();

    public Dictionary<string, int> Agents { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Unknown { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths that were edited or read, normalised for suffix matching (claude_memory.py:1280).</summary>
    public HashSet<string> Opened()
    {
        var opened = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Edits.Keys) opened.Add(Normalise(key));
        foreach (var key in Reads.Keys) opened.Add(Normalise(key));
        return opened;
    }

    /// <summary>Collects file, command, subagent and other-tool facts from tool calls.</summary>
    public static FileFacts Collect(IReadOnlyList<Session> sessions)
    {
        var facts = new FileFacts();

        foreach (var session in sessions)
        {
            foreach (var (call, _) in session.ToolCalls())
            {
                var key = ArgumentValue(call.Brief, 160);
                if (EditTools.Contains(call.Name)) Bump(facts.Edits, key);
                else if (ReadTools.Contains(call.Name)) Bump(facts.Reads, key);
                else if (call.Name == "Bash")
                {
                    if (facts.Commands.Count < 100) facts.Commands.Add(Text.TextUtil.OneLine(key, 150));
                }
                else if (call.Name == "Task") Bump(facts.Agents, Truncate(call.Brief, 100));
                else Bump(facts.Unknown, call.Name);
            }
        }

        // a file that was edited was obviously read too: do not list it twice (claude_memory.py:1306)
        foreach (var key in facts.Edits.Keys) facts.Reads.Remove(key);

        return facts;
    }

    /// <summary>
    /// The value half of a <c>key=value</c> brief, capped. Python's <c>split("=", 1)[-1]</c>
    /// returns the whole string when there is no separator (claude_memory.py:1294).
    /// </summary>
    private static string ArgumentValue(string brief, int limit)
    {
        var separator = brief.IndexOf('=');
        var value = separator < 0 ? brief : brief[(separator + 1)..];
        return Truncate(value, limit);
    }

    private static string Normalise(string key) => key.Replace('\\', '/').ToLowerInvariant();

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit];

    private static void Bump(Dictionary<string, int> store, string key)
        => store[key] = store.TryGetValue(key, out var count) ? count + 1 : 1;
}
