using System.Text.Json;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Reproduces the Python semantics the reference parser depends on: JSON truthiness and
/// <c>str()</c> conversion (claude_memory.py:463-510 and 678-757).
/// </summary>
internal static class PyJson
{
    /// <summary>Python truthiness of a JSON value (an empty string/list/object is false).</summary>
    public static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary><c>str(value)</c> for a JSON value.</summary>
    public static string Str(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null or JsonValueKind.Undefined => "None",
        _ => Repr(value),
    };

    /// <summary><c>str(obj.get(key, whenMissing))</c>.</summary>
    public static string Get(JsonElement obj, string key, string whenMissing = "")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value)) return whenMissing;
        return Str(value);
    }

    /// <summary><c>value or fallback</c> for an optional JSON value.</summary>
    public static string Or(JsonElement? value, string fallback)
        => value is { } element && Truthy(element) ? Str(element) : fallback;

    /// <summary>
    /// Approximation of Python's <c>repr()</c>, used only where the reference would format a
    /// container through <c>str()</c>.
    /// </summary>
    public static string Repr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Quote(value.GetString()!),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => Str(value),
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Repr)) + "]",
        JsonValueKind.Object => "{" + string.Join(
            ", ",
            value.EnumerateObject().Select(p => Quote(p.Name) + ": " + Repr(p.Value))) + "}",
        _ => "None",
    };

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
