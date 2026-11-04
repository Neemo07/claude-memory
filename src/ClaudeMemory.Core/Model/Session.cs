using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Model;

/// <summary>One parsed transcript file (claude_memory.py:590).</summary>
public sealed class Session
{
    public Session(string path)
    {
        Path = path;
        Sid = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Absolute path of the source .jsonl file, or the saved share payload.</summary>
    public string Path { get; set; }

    public string Sid { get; set; } = "";

    public string Cwd { get; set; } = "";

    public string Branch { get; set; } = "";

    public string Version { get; set; } = "";

    public string Model { get; set; } = "";

    public DateTimeOffset? Start { get; set; }

    public DateTimeOffset? End { get; set; }

    public List<Turn> Turns { get; } = new();

    public List<string> Summaries { get; } = new();

    /// <summary>Records dropped as noise or meta (claude_memory.py:601).</summary>
    public int Skipped { get; set; }

    public bool TruncatedFile { get; set; }

    /// <summary>Whole-second span, or 0 when either end is unknown.</summary>
    public double Duration => Start is { } start && End is { } end ? (end - start).TotalSeconds : 0.0;

    /// <summary>True user prompts: text only, no tool calls, no errors.</summary>
    public List<string> Prompts()
    {
        var prompts = new List<string>();
        foreach (var turn in Turns)
        {
            if (turn.Role != TurnRoles.User) continue;
            if (turn.Text.Trim().Length == 0) continue;
            if (turn.Tools.Count > 0 || turn.Errors.Count > 0) continue;
            prompts.Add(turn.Text.Trim());
        }

        return prompts;
    }

    public List<(ToolCall Call, bool Sidechain)> ToolCalls()
    {
        var calls = new List<(ToolCall, bool)>();
        foreach (var turn in Turns)
        {
            foreach (var call in turn.Tools) calls.Add((call, turn.Sidechain));
        }

        return calls;
    }

    public List<string> ErrorLines()
    {
        var errors = new List<string>();
        foreach (var turn in Turns) errors.AddRange(turn.Errors);
        return errors;
    }

    public List<string> AssistantText()
    {
        var texts = new List<string>();
        foreach (var turn in Turns)
        {
            if (turn.Role == TurnRoles.Assistant && turn.Text.Trim().Length > 0) texts.Add(turn.Text);
        }

        return texts;
    }

    /// <summary>Short human label: first prompt, else first summary, else "(no prompt)".</summary>
    public string Label()
    {
        var prompts = Prompts();
        var first = prompts.Count > 0 ? prompts[0] : "";
        if (first.Length > 0) return TextUtil.OneLine(first, 70);
        return Summaries.Count > 0 ? Summaries[0] : "(no prompt)";
    }
}
