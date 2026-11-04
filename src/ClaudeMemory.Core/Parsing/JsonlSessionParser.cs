using System.Text;
using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Defensive JSONL transcript reader, ported from claude_memory.py:643-758
/// (<c>_iter_records</c>, <c>parse_session</c>).
/// </summary>
public static class JsonlSessionParser
{
    /// <summary>
    /// Reads a transcript file defensively: a malformed line is skipped, a file larger than
    /// <paramref name="maxBytes"/> is read from the tail with the partial first line dropped,
    /// and undecodable bytes become U+FFFD. Returns the records and whether the tail was read.
    /// </summary>
    public static (List<JsonElement> Records, bool Truncated) IterRecords(string path, int maxBytes)
    {
        var records = new List<JsonElement>();
        var truncated = false;

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return (records, truncated);
        }
        catch (UnauthorizedAccessException)
        {
            return (records, truncated);
        }

        var tailFrom = 0 < maxBytes && maxBytes < size ? size - maxBytes : 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (tailFrom > 0)
            {
                truncated = true;
                stream.Seek(tailFrom, SeekOrigin.Begin);
            }

            // No BOM detection: Python's `encoding="utf-8"` keeps a BOM as a character.
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false);

            if (tailFrom > 0) reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                JsonElement record;
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    record = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (record.ValueKind != JsonValueKind.Object) continue;
                records.Add(record);
            }
        }
        catch (IOException)
        {
            return (records, truncated);
        }
        catch (UnauthorizedAccessException)
        {
            return (records, truncated);
        }

        return (records, truncated);
    }

    /// <summary>Parses one transcript file into a <see cref="Session"/>.</summary>
    public static Session ParseSession(string path, MemoryOptions opts)
    {
        var session = new Session(path);
        var (records, truncated) = IterRecords(path, opts.MaxFileBytes);
        session.TruncatedFile = truncated;

        foreach (var record in records)
        {
            var recordType = GetString(record, "type");
            var timestamp = TimeWindow.ParseTimestamp(GetString(record, "timestamp"));
            if (timestamp is { } ts)
            {
                if (session.Start is null || ts < session.Start) session.Start = ts;
                if (session.End is null || ts > session.End) session.End = ts;
            }

            foreach (var key in new[] { "sessionId", "session_id" })
            {
                var value = GetString(record, key);
                if (!string.IsNullOrEmpty(value)) session.Sid = value;
            }

            var cwd = GetString(record, "cwd");
            if (!string.IsNullOrEmpty(cwd)) session.Cwd = cwd;

            var branch = GetString(record, "gitBranch");
            if (!string.IsNullOrEmpty(branch)) session.Branch = branch;

            var version = GetString(record, "version");
            if (!string.IsNullOrEmpty(version)) session.Version = version;

            if (recordType == "summary")
            {
                var summary = GetString(record, "summary");
                if (!string.IsNullOrEmpty(summary) && summary.Trim().Length > 0) session.Summaries.Add(summary.Trim());
                continue;
            }

            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
            {
                var content = GetString(record, "content");
                if (content is not null && (recordType == "system" || recordType == "user"))
                    session.Turns.Add(new Turn { Role = TurnRoles.System, Text = content, Timestamp = timestamp });
                else
                    session.Skipped++;
                continue;
            }

            var roleText = GetString(message, "role");
            if (string.IsNullOrEmpty(roleText)) roleText = recordType;

            var model = GetString(message, "model");
            if (!string.IsNullOrEmpty(model)) session.Model = model;

            if (TryGetProperty(record, "isMeta", out var meta) && PyJson.Truthy(meta))
            {
                session.Skipped++;
                continue;
            }

            var sidechain = TryGetProperty(record, "isSidechain", out var sidechainValue) && PyJson.Truthy(sidechainValue);
            if (sidechain && opts.Sidechains == MemoryOptions.SidechainsDrop)
            {
                session.Skipped++;
                continue;
            }

            var texts = new List<string>();
            var tools = new List<ToolCall>();
            var errors = new List<string>();

            if (!message.TryGetProperty("content", out var contentValue))
            {
                session.Skipped++;
                continue;
            }

            if (contentValue.ValueKind == JsonValueKind.String)
            {
                texts.Add(contentValue.GetString()!);
            }
            else if (contentValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentValue.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;

                    var blockType = GetString(block, "type");
                    if (blockType == "text")
                    {
                        texts.Add(PyJson.Get(block, "text"));
                    }
                    else if (blockType == "thinking" && opts.IncludeThinking)
                    {
                        texts.Add(PyJson.Get(block, "thinking"));
                    }
                    else if (blockType == "tool_use")
                    {
                        var name = PyJson.Get(block, "name", "tool");
                        var input = TryGetProperty(block, "input", out var rawInput) ? rawInput : (JsonElement?)null;
                        tools.Add(new ToolCall(name, ToolBrief.Brief(name, input, sidechain ? 120 : 200)));
                    }
                    else if (blockType == "tool_result")
                    {
                        if (TryGetProperty(block, "is_error", out var isError) && PyJson.Truthy(isError))
                        {
                            var body = ToolBrief.BodyQuotes(block);
                            errors.Add(TextUtil.OneLine(body.Length > 0 ? body : "tool error", 200));
                        }
                    }
                }
            }
            else
            {
                session.Skipped++;
                continue;
            }

            var text = string.Join("\n", texts.Where(t => t.Length > 0 && t.Trim().Length > 0)).Trim();
            if (text.Length == 0 && tools.Count == 0 && errors.Count == 0)
            {
                session.Skipped++;
                continue;
            }

            // Unknown or missing roles fall back on the payload (claude_memory.py:753).
            var role = roleText is TurnRoles.User or TurnRoles.Assistant or TurnRoles.System
                ? roleText
                : tools.Count > 0 ? TurnRoles.Assistant : TurnRoles.User;

            session.Turns.Add(new Turn
            {
                Role = role,
                Text = text,
                Tools = tools,
                Errors = errors,
                Timestamp = timestamp,
                Sidechain = sidechain,
            });
        }

        return session;
    }

    private static string? GetString(JsonElement element, string property)
        => TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object) return element.TryGetProperty(property, out value);
        value = default;
        return false;
    }
}
