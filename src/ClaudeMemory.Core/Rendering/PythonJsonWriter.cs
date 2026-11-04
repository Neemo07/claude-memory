using System.Globalization;
using System.Text;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Minimal JSON writer that matches Python's <c>json.dumps(..., indent=2, ensure_ascii=False)</c>
/// (claude_memory.py:1701) byte for byte.
/// </summary>
/// <remarks>
/// <c>System.Text.Json</c> escapes non-ASCII text and lays out objects differently, so the
/// exporter uses this writer instead. Only the value shapes the exporter produces are
/// supported; a nested object is an ordered list of pairs so key order is preserved.
/// </remarks>
internal static class PythonJsonWriter
{
    public static void Write(StringBuilder builder, object? value, int level)
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
            case double number:
                builder.Append(Float(number));
                break;
            case IReadOnlyList<object?> list:
                WriteArray(builder, list, level);
                break;
            case IReadOnlyList<KeyValuePair<string, object?>> map:
                WriteObject(builder, map, level);
                break;
            default:
                throw new NotSupportedException("Unsupported JSON value: " + value.GetType());
        }
    }

    private static void WriteArray(StringBuilder builder, IReadOnlyList<object?> list, int level)
    {
        if (list.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append("[\n");
        for (var index = 0; index < list.Count; index++)
        {
            Indent(builder, level + 1);
            Write(builder, list[index], level + 1);
            if (index < list.Count - 1) builder.Append(',');
            builder.Append('\n');
        }

        Indent(builder, level);
        builder.Append(']');
    }

    private static void WriteObject(
        StringBuilder builder, IReadOnlyList<KeyValuePair<string, object?>> map, int level)
    {
        if (map.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append("{\n");
        for (var index = 0; index < map.Count; index++)
        {
            Indent(builder, level + 1);
            WriteString(builder, map[index].Key);
            builder.Append(": ");
            Write(builder, map[index].Value, level + 1);
            if (index < map.Count - 1) builder.Append(',');
            builder.Append('\n');
        }

        Indent(builder, level);
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

    /// <summary>Shortest round-trip float with a trailing <c>.0</c>, matching Python's <c>repr()</c>.</summary>
    private static string Float(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";

        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 ? text + ".0" : text;
    }

    private static void Indent(StringBuilder builder, int level) => builder.Append(' ', level * 2);
}
