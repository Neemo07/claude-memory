namespace ClaudeMemory.Core.Model;

/// <summary>
/// Turn roles produced by the parser. Values match the reference implementation
/// (claude_memory.py:581) because they are compared as strings while rendering.
/// </summary>
public static class TurnRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
    public const string Note = "note";
}

/// <summary>A single normalised transcript turn (claude_memory.py:580).</summary>
public sealed class Turn
{
    public string Role { get; init; } = TurnRoles.User;

    public string Text { get; init; } = "";

    public IReadOnlyList<ToolCall> Tools { get; init; } = Array.Empty<ToolCall>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public DateTimeOffset? Timestamp { get; init; }

    public bool Sidechain { get; init; }
}
