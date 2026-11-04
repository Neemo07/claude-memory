namespace ClaudeMemory.Core.Model;

/// <summary>
/// Global character budget shared by every rendered section (claude_memory.py:539).
/// </summary>
/// <remarks>
/// The reference prints a warning to stderr when the requested total is below the
/// floor. Core never writes to a console (AGENTS.md), so <see cref="RaisedToFloor"/>
/// reports the condition and the host decides how to warn.
/// </remarks>
public sealed class Budget
{
    public const int Floor = 200;

    public Budget(int total)
    {
        Requested = total;
        Total = Math.Max(Floor, total);
        RaisedToFloor = total < Floor;
    }

    public int Total { get; }

    public int Requested { get; }

    public int Used { get; private set; }

    /// <summary>Number of lines refused because they did not fit.</summary>
    public int Trimmed { get; private set; }

    /// <summary>True when <see cref="Requested"/> was raised to <see cref="Floor"/>.</summary>
    public bool RaisedToFloor { get; }

    public int Left => Total - Used;

    /// <summary>Keeps the lines that fit and counts the rest as trimmed.</summary>
    public List<string> Take(IEnumerable<string> lines, string bullet = "- ", string indent = "")
    {
        var kept = new List<string>();
        foreach (var line in lines)
        {
            var cost = line.Length + indent.Length + bullet.Length + 1;
            if (cost > Left)
            {
                Trimmed++;
                continue;
            }

            Used += cost;
            kept.Add(bullet.Length > 0 ? indent + bullet + line : indent + line);
        }

        return kept;
    }

    /// <summary>Consumes budget for a single already-formatted line.</summary>
    public bool Line(string text)
    {
        var cost = text.Length + 1;
        if (cost > Left)
        {
            Trimmed++;
            return false;
        }

        Used += cost;
        return true;
    }
}
