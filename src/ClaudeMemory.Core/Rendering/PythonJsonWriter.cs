using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Minimal JSON writer that matches Python's <c>json.dumps(..., indent=2, ensure_ascii=False)</c>
/// (claude_memory.py:1701) byte for byte, and the <c>indent=1</c> cache form
/// (claude_memory.py:1931).
/// </summary>
/// <remarks>
/// <c>System.Text.Json</c> escapes non-ASCII text and lays out objects differently, so the
/// exporter uses this writer instead. Only the value shapes the exporter produces are
/// supported; a nested object is an ordered list of pairs so key order is preserved.
/// </remarks>
internal static class PythonJsonWriter
{
    public static void Write(StringBuilder builder, object? value, int level)
        => Write(builder, value, level, 2);

    /// <summary>Serialises a parsed payload with a chosen indent width, keeping key order.</summary>
    public static string Dump(JsonElement element, int indent)
    {
        var builder = new StringBuilder();
        Write(builder, FromElement(element), 0, indent);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value, int level, int indent)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case double number:
                builder.Append(Float(number));
                break;
            case IReadOnlyList<object?> list:
                WriteArray(builder, list, level, indent);
                break;
            case IReadOnlyList<KeyValuePair<string, object?>> map:
                WriteObject(builder, map, level, indent);
                break;
            default:
                throw new NotSupportedException("Unsupported JSON value: " + value.GetType());
        }
    }

    private static void WriteArray(
        StringBuilder builder, IReadOnlyList<object?> list, int level, int indent)
    {
        if (list.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append("[\n");
        for (var index = 0; index < list.Count; index++)
        {
            Indent(builder, level + 1, indent);
            Write(builder, list[index], level + 1, indent);
            if (index < list.Count - 1) builder.Append(',');
            builder.Append('\n');
        }

        Indent(builder, level, indent);
        builder.Append(']');
    }

    private static void WriteObject(
        StringBuilder builder, IReadOnlyList<KeyValuePair<string, object?>> map, int level, int indent)
    {
        if (map.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append("{\n");
        for (var index = 0; index < map.Count; index++)
        {
            Indent(builder, level + 1, indent);
            WriteString(builder, map[index].Key);
            builder.Append(": ");
            Write(builder, map[index].Value, level + 1, indent);
            if (index < map.Count - 1) builder.Append(',');
            builder.Append('\n');
        }

        Indent(builder, level, indent);
        builder.Append('}');
    }

    /// <summary>Python <c>json</c> escaping with <c>ensure_ascii=False</c>: non-ASCII stays raw.</summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>Python repr-style float text.</summary>
    private static string Float(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";

        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) text += ".0";

        // .NET writes "1E+20"; Python's repr uses a lowercase exponent.
        return text.Replace('E', 'e');
    }

    /// <summary>Converts a parsed JSON tree into the writer's ordered value model.</summary>
    private static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Select(property => new KeyValuePair<string, object?>(property.Name, FromElement(property.Value)))
            .ToList(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => Number(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>Python keeps ints as ints and floats as floats when it re-serialises a payload.</summary>
    private static object Number(JsonElement element)
    {
        var raw = element.GetRawText();
        if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) return element.GetDouble();

        return element.TryGetInt64(out var value) ? value : (object)element.GetDouble();
    }

    private static void Indent(StringBuilder builder, int level, int width)
        => builder.Append(' ', level * width);
}
