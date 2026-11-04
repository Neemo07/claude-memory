using System.Text.Json;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Reduces a shared conversation to its active branch, ported from
/// claude_memory.py:834-869 (<c>share_chain</c>).
/// </summary>
/// <remarks>
/// Shared conversations are usually one linear chain, but a reply-to-reply edit leaves
/// sibling messages. The reference follows the deepest leaf and breaks ties by index, so an
/// abandoned branch does not pollute the memory.
/// </remarks>
public static class ShareBranch
{
    public static List<JsonElement> Active(IReadOnlyList<JsonElement> messages)
    {
        if (messages.Count == 0) return new List<JsonElement>();

        var byUuid = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (!TryUuid(message, out var uuid)) continue;
            byUuid[uuid] = message;
        }

        var children = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (!message.TryGetProperty("parent_message_uuid", out var parent)) continue;
            if (!PyJson.Truthy(parent)) continue;

            var key = PyJson.Str(parent);
            children[key] = children.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var leaves = new List<JsonElement>();
        foreach (var message in messages)
        {
            var childCount = TryUuid(message, out var uuid) && children.TryGetValue(uuid, out var count)
                ? count
                : 0;
            if (childCount == 0) leaves.Add(message);
        }

        if (leaves.Count == 0) leaves.AddRange(messages);

        var best = leaves[0];
        var bestRank = Rank(best, byUuid);
        foreach (var leaf in leaves.Skip(1))
        {
            var rank = Rank(leaf, byUuid);
            if (rank.Depth > bestRank.Depth
                || (rank.Depth == bestRank.Depth && rank.Index > bestRank.Index))
            {
                best = leaf;
                bestRank = rank;
            }
        }

        return ChainToRoot(best, byUuid);
    }

    private static (int Depth, long Index) Rank(JsonElement message, Dictionary<string, JsonElement> byUuid)
        => (ChainToRoot(message, byUuid).Count, IndexOf(message));

    /// <summary>The reference's <c>index</c> tie-break: an integer, and <c>bool</c> counts as one.</summary>
    private static long IndexOf(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return 0;
        if (!message.TryGetProperty("index", out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => 0,
        };
    }

    private static List<JsonElement> ChainToRoot(
        JsonElement message, Dictionary<string, JsonElement> byUuid)
    {
        var output = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenNull = false;
        JsonElement? current = message;

        while (current is { ValueKind: JsonValueKind.Object } node)
        {
            var hasUuid = TryUuid(node, out var uuid);
            if (hasUuid)
            {
                if (!seen.Add(uuid)) break;
            }
            else
            {
                if (seenNull) break;
                seenNull = true;
            }

            output.Add(node);

            if (!node.TryGetProperty("parent_message_uuid", out var parent) || !PyJson.Truthy(parent))
            {
                current = null;
                continue;
            }

            current = byUuid.TryGetValue(PyJson.Str(parent), out var found) ? found : null;
        }

        output.Reverse();
        return output;
    }

    private static bool TryUuid(JsonElement message, out string uuid)
    {
        uuid = "";
        if (message.ValueKind != JsonValueKind.Object) return false;
        if (!message.TryGetProperty("uuid", out var value) || !PyJson.Truthy(value)) return false;

        uuid = PyJson.Str(value);
        return true;
    }
}