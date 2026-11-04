namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// claude.ai exposes its own tool names, not Claude Code's Read/Edit/Bash, so share
/// imports map them before rendering (claude_memory.py:208-217).
/// </summary>
public static class WebToolAliases
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["bash_tool"] = "Bash",
        ["bash"] = "Bash",
        ["shell"] = "Bash",
        ["run_command"] = "Bash",
        ["view"] = "Read",
        ["read_file"] = "Read",
        ["view_file"] = "Read",
        ["str_replace_editor"] = "Edit",
        ["str_replace_based_edit_tool"] = "Edit",
        ["create_file"] = "Write",
        ["write_file"] = "Write",
        ["file_editor"] = "Write",
        ["present_files"] = "PresentFiles",
        ["present_files_tool"] = "PresentFiles",
        ["web_search"] = "WebSearch",
        ["web_fetch"] = "WebFetch",
        ["fetch"] = "WebFetch",
        ["todo"] = "TodoWrite",
        ["artifact"] = "Artifact",
        ["memory"] = "Memory",
        ["computer"] = "Computer",
        ["task"] = "Task",
        ["agent"] = "Task",
    };

    /// <summary>Maps a payload tool name, leaving unknown names untouched.</summary>
    public static string Resolve(string rawName)
        => Map.TryGetValue(rawName, out var name) ? name : rawName;
}