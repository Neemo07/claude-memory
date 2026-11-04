using System.Text;
using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Services;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Turns a <c>/api/chat_snapshots</c> payload into the same <see cref="Session"/> model as a
/// JSONL transcript, ported from claude_memory.py:872-1017 (<c>share_chain</c> helpers,
/// <c>parse_share_payload</c>, <c>share_result_text</c>, <c>load_share_file</c>).
/// </summary>
public static class SharePayloadParser
{
    /// <summary>Builds a session from a fetched or saved share payload.</summary>
    /// <exception cref="ShareException">The payload is not a usable chat snapshot.</exception>
    public static Session Parse(JsonElement payload, string labelHint = "", bool includeThinking = false)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ShareException("share payload is not a JSON object");

        if (!TryMessages(payload, out var messages))
        {
            // A saved share page is an HTML shell: the conversation is rendered client-side.
            var hasChatMessages = payload.TryGetProperty("chat_messages", out var chat);
            var noChatMessages = !hasChatMessages || chat.ValueKind == JsonValueKind.Null;
            if (noChatMessages && payload.TryGetProperty("html", out _))
            {
                throw new ShareException(
                    "that looks like a saved share *page*, which contains no conversation "
                    + "(claude.ai renders it client-side). Save the chat_snapshots JSON "
                    + "response instead. " + ShareClient.ManualHint);
            }

            throw new ShareException(
                "no chat_messages in the payload - is this really a chat_snapshots "
                + "response? " + ShareClient.ManualHint);
        }

        var uuid = PyJson.Or(
            Named(payload, "uuid"),
            PyJson.Or(Named(payload, "conversation_uuid"), "shared"));

        var session = new Session($"share-{uuid}.json")
        {
            Sid = uuid,
            Cwd = "",
            Branch = "shared-link",
            Model = "claude.ai",
            Start = Timestamp(payload, "created_at"),
            End = Timestamp(payload, "updated_at"),
        };

        var chain = ShareBranch.Active(messages);
        session.Skipped = messages.Count - chain.Count;

        var title = PyJson.Or(Named(payload, "snapshot_name"), labelHint);
        if (title.Length > 0) session.Summaries.Insert(0, "[shared] " + title);

        foreach (var message in chain)
        {
            var role = MessageString(message, "sender") == "assistant"
                ? TurnRoles.Assistant
                : TurnRoles.User;

            var blocks = BlockList(message);
            var texts = new List<string>();
            var tools = new List<ToolCall>();
            var errors = new List<string>();

            foreach (var block in blocks)
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (Named(block, "hidden_in_chat") is { } hidden && PyJson.Truthy(hidden)) continue;

                switch (MessageString(block, "type"))
                {
                    case "text":
                    {
                        var body = Named(block, "text");
                        if (body is { ValueKind: JsonValueKind.String } textBlock
                            && textBlock.GetString()!.Trim().Length > 0)
                        {
                            texts.Add(textBlock.GetString()!);
                        }

                        break;
                    }

                    case "tool_use":
                    {
                        var rawName = PyJson.Or(Named(block, "name"), "tool");
                        var name = WebToolAliases.Resolve(rawName);
                        tools.Add(new ToolCall(name, ToolBrief.Brief(name, Named(block, "input"))));
                        break;
                    }

                    case "tool_result":
                    {
                        if (Named(block, "is_error") is { } isError && PyJson.Truthy(isError))
                        {
                            var body = FirstTruthy(block, "message", "content");
                            errors.Add(TextUtil.OneLine(ResultText(body), 220));
                        }

                        break;
                    }

                    case "thinking":
                    case "redacted_thinking":
                    {
                        if (!includeThinking) break;

                        var body = FirstTruthy(block, "thinking", "data");
                        if (body is { ValueKind: JsonValueKind.String } thinkingText
                            && thinkingText.GetString()!.Trim().Length > 0)
                        {
                            texts.Add(thinkingText.GetString()!);
                        }

                        break;
                    }
                }
            }

            var text = string.Join("\n", texts).Trim();

            // Older exports carry a flat "text" field instead of content blocks, so the
            // fallback runs before the "nothing to keep" check.
            if (text.Length == 0 && MessageString(message, "text") is { } flat && flat.Trim().Length > 0)
                text = flat.Trim();

            if (text.Length == 0 && tools.Count == 0 && errors.Count == 0)
            {
                session.Skipped++;
                continue;
            }

            session.Turns.Add(new Turn
            {
                Role = role,
                Text = text,
                Tools = tools,
                Errors = errors,
                Timestamp = Timestamp(message, "created_at") ?? BlockTimestamp(blocks),
            });

            if (Named(message, "compaction_summary") is { } compaction && PyJson.Truthy(compaction))
            {
                if (compaction.ValueKind == JsonValueKind.String)
                {
                    session.Summaries.Add(compaction.GetString()!);
                }
                else if (compaction.ValueKind == JsonValueKind.Object)
                {
                    var summary = PyJson.Or(Named(compaction, "summary"), PyJson.Str(compaction));
                    session.Summaries.Add(TextUtil.OneLine(summary, 300));
                }
            }
        }

        // Last resort: some payloads only carry timestamps inside the blocks.
        var stamps = session.Turns
            .Where(turn => turn.Timestamp.HasValue)
            .Select(turn => turn.Timestamp!.Value)
            .ToList();
        if (stamps.Count > 0)
        {
            session.Start ??= stamps.Min();
            session.End ??= stamps.Max();
        }

        return session;
    }

    /// <summary>
    /// Imports a payload saved earlier (offline). Tolerates a raw JSON array of messages
    /// (claude_memory.py:997).
    /// </summary>
    /// <exception cref="ShareException">The file cannot be read or is not a share payload.</exception>
    public static Session LoadFile(string path, bool includeThinking = false)
    {
        string text;
        try
        {
            using var reader = new StreamReader(
                path,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: false);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
        {
            throw new ShareException($"cannot read {path}: {error.Message}");
        }

        var stripped = text.TrimStart();
        if (stripped.Length == 0 || (stripped[0] != '{' && stripped[0] != '['))
        {
            throw new ShareException(
                $"{System.IO.Path.GetFileName(path)} does not look like JSON "
                + "(share page HTML has no conversation in it)");
        }

        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(stripped);
            data = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ShareException($"{System.IO.Path.GetFileName(path)} is not valid JSON: {error.Message}");
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (data.ValueKind == JsonValueKind.Array) data = WrapMessages(data, stem);

        var session = Parse(data, labelHint: stem, includeThinking: includeThinking);
        session.Path = path;
        return session;
    }

    /// <summary>
    /// <c>tool_result</c> payloads arrive as a string, a block list, or an object
    /// (claude_memory.py:974).
    /// </summary>
    public static string ResultText(JsonElement? body)
    {
        switch (body?.ValueKind)
        {
            case null:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return TextUtil.OneLine("", 220);
            case JsonValueKind.String:
                return body!.Value.GetString()!;
            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach (var block in body!.Value.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object)
                    {
                        var part = PyJson.Or(Named(block, "text"), PyJson.Or(Named(block, "content"), ""));
                        parts.Add(part);
                    }
                    else if (block.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(block.GetString()!);
                    }
                }

                return string.Join("\n", parts.Where(part => part.Length > 0));
            }

            case JsonValueKind.Object:
            {
                foreach (var key in new[] { "text", "content", "message", "stdout", "stderr", "output" })
                {
                    if (!body!.Value.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length > 0)
                        return value.GetString()!;
                    if (value.ValueKind == JsonValueKind.Array) return ResultText(value);
                }

                return TextUtil.OneLine(PyJson.Str(body!.Value), 220);
            }

            default:
                return body is { } scalar && PyJson.Truthy(scalar)
                    ? TextUtil.OneLine(PyJson.Str(scalar), 220)
                    : "";
        }
    }

    /// <summary>First usable timestamp on a content block (claude_memory.py:872).</summary>
    private static DateTimeOffset? BlockTimestamp(IReadOnlyList<JsonElement> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            var stamp = Timestamp(block, "stop_timestamp") ?? Timestamp(block, "start_timestamp");
            if (stamp is not null) return stamp;
        }

        return null;
    }

    private static bool TryMessages(JsonElement payload, out List<JsonElement> messages)
    {
        messages = new List<JsonElement>();
        if (!payload.TryGetProperty("chat_messages", out var value)) return false;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;

        messages.AddRange(value.EnumerateArray());
        return true;
    }

    private static List<JsonElement> BlockList(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return new List<JsonElement>();
        if (!message.TryGetProperty("content", out var content)) return new List<JsonElement>();
        if (content.ValueKind != JsonValueKind.Array) return new List<JsonElement>();

        return content.EnumerateArray().ToList();
    }

    /// <summary>The value of <paramref name="key"/> when the element is an object.</summary>
    private static JsonElement? Named(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(key, out var value) ? value : null;
    }

    /// <summary><c>obj.get(first) or obj.get(second)</c> for raw payload values.</summary>
    private static JsonElement? FirstTruthy(JsonElement element, string first, string second)
    {
        var value = Named(element, first);
        if (value is { } present && PyJson.Truthy(present)) return present;
        return Named(element, second);
    }

    /// <summary><c>value</c> when it is a JSON string, matching Python's <c>isinstance(x, str)</c>.</summary>
    private static string? MessageString(JsonElement element, string key)
    {
        var value = Named(element, key);
        return value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string key)
    {
        var value = Named(element, key);
        return value is { ValueKind: JsonValueKind.String } text ? TimeWindow.ParseTimestamp(text.GetString()) : null;
    }

    /// <summary>Wraps a raw message array exactly as the reference does (claude_memory.py:1011).</summary>
    private static JsonElement WrapMessages(JsonElement messages, string stem)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("chat_messages");
            messages.WriteTo(writer);
            writer.WriteString("uuid", stem);
            writer.WriteString("snapshot_name", stem);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}