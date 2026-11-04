using System.Text.Json;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// One-line summaries of tool inputs, ported from claude_memory.py:463-510
/// (<c>body_quotes</c>, <c>tool_brief</c>).
/// </summary>
public static class ToolBrief
{
    private const string AgentFallback = "agent";

    /// <summary>Preferred argument keys for claude.ai tool names (claude_memory.py:218).</summary>
    private static readonly Dictionary<string, string[]> WebToolKeys = new(StringComparer.Ordinal)
    {
        ["Bash"] = new[] { "command", "commandLine", "cmd", "script" },
        ["Read"] = new[] { "path", "file_path", "filePath", "target_file" },
        ["Edit"] = new[] { "path", "file_path", "command" },
        ["Write"] = new[] { "path", "file_path", "filePath", "target_file" },
        ["PresentFiles"] = new[] { "filepaths", "file_paths", "paths", "files" },
        ["WebSearch"] = new[] { "query", "url" },
        ["WebFetch"] = new[] { "url", "prompt", "query" },
    };

    private static readonly string[] FallbackKeys =
    {
        "file_path", "notebook_path", "path", "url", "pattern", "query",
        "command", "description", "prompt",
    };

    private static readonly HashSet<string> EditNames =
        new(new[] { "Edit", "MultiEdit", "Write", "NotebookEdit" }, StringComparer.Ordinal);

    /// <summary>Best-effort text of a <c>tool_result</c> content payload (claude_memory.py:463).</summary>
    public static string BodyQuotes(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("content", out var content))
            return "";

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString()!,
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object && PyJson.Get(block, "type") == "text")
                    .Select(block => PyJson.Get(block, "text"))),
            _ => "",
        };
    }

    /// <summary>One-line summary of a tool_use input, without echoing file bodies.</summary>
    public static string Brief(string name, JsonElement? input, int limit = 160)
    {
        if (input is not { ValueKind: JsonValueKind.Object } args)
            return TextUtil.OneLine(PyJson.Or(input, ""), 80);

        if (name == "Task")
        {
            var who = TextUtil.OneLine(Or(args, "subagent_type", AgentFallback), 40);

            // The reference never calls Task with a limit small enough to make this negative.
            var whatLimit = Math.Max(1, limit - who.Length - 3);
            var what = TextUtil.OneLine(Or(args, "description", Or(args, "prompt", "")), whatLimit);
            return what.Length > 0 ? $"{who}: {what}" : who;
        }

        var hit = Scalar(args, WebToolKeys.TryGetValue(name, out var keys) ? keys : Array.Empty<string>(), limit);
        if (hit.Length > 0) return hit;

        hit = Scalar(args, FallbackKeys, limit);
        if (hit.Length > 0) return hit;

        if (EditNames.Contains(name)) return Truncate(JoinedKeys(args), limit);

        if (args.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            return $"{edits.GetArrayLength()} edits";

        var names = Truncate(JoinedKeys(args), limit);
        return names.Length > 0 ? names : "(no args)";
    }

    private static string Scalar(JsonElement args, IReadOnlyList<string> keys, int limit)
    {
        foreach (var key in keys)
        {
            if (!args.TryGetProperty(key, out var value)) continue;

            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length > 0)
                return TextUtil.OneLine($"{key}={value.GetString()}", limit);

            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
            {
                // The reference inspects only the first four elements, then keeps the strings
                // and integers among them (claude_memory.py:493).
                var items = new List<string>();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ >= 4) break;
                    if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        items.Add(TextUtil.OneLine(PyJson.Str(item), 60));
                }

                if (items.Count > 0)
                {
                    var extra = value.GetArrayLength() > items.Count ? $" (+{value.GetArrayLength() - items.Count})" : "";
                    return TextUtil.OneLine($"{key}=" + string.Join(", ", items) + extra, limit);
                }
            }
        }

        return "";
    }

    private static string Or(JsonElement obj, string key, string fallback)
    {
        var value = obj.TryGetProperty(key, out var found) ? found : (JsonElement?)null;
        return PyJson.Or(value, fallback);
    }

    private static string JoinedKeys(JsonElement args)
        => string.Join(", ", args.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
