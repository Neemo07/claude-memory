using System.Globalization;
using System.Text;
using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Rendering;

/// <summary>
/// Renders <c>--mode json</c>, ported from claude_memory.py:1678-1702 (<c>build_json</c>).
/// </summary>
/// <remarks>
/// Key order is Python insertion order and must stay stable; the writer in
/// <see cref="PythonJsonWriter"/> reproduces <c>json.dumps(..., indent=2, ensure_ascii=False)</c>.
/// </remarks>
public static class JsonExporter
{
    /// <summary>Builds the machine-readable transcript export for the mode <c>json</c>.</summary>
    public static string Build(IReadOnlyList<Session> sessions, DateTimeOffset now)
    {
        var payload = new List<object?>();
        foreach (var session in sessions) payload.Add(SessionObject(session));

        var document = new List<KeyValuePair<string, object?>>
        {
            new("generated", BuildInfo.IsoNow(now)),
            new("tool", $"{BuildInfo.GeneratorName} {BuildInfo.ReferenceVersion}"),
            new("sessions", payload),
        };

        var text = new StringBuilder();
        PythonJsonWriter.Write(text, document, 0);
        text.Append('\n');
        return text.ToString();
    }

    private static List<KeyValuePair<string, object?>> SessionObject(Session session)
        => new()
        {
            new("id", session.Sid),
            new("file", session.Path),
            new("cwd", session.Cwd),
            new("branch", session.Branch),
            new("model", session.Model),
            new("claude_code_version", session.Version),
            new("start", session.Start is { } start ? PythonIso(start) : null),
            new("end", session.End is { } end ? PythonIso(end) : null),
            new("duration_seconds", Math.Round(session.Duration, 1, MidpointRounding.ToEven)),
            new("turns", session.Turns.Count),
            new("tool_calls", session.ToolCalls().Count),
            new("records_skipped", session.Skipped),
            new("summaries", session.Summaries.Cast<object?>().ToList()),
            new("prompts", session.Prompts().Cast<object?>().ToList()),
            new("messages", session.Turns.Select(TurnObject).Cast<object?>().ToList()),
        };

    private static List<KeyValuePair<string, object?>> TurnObject(Turn turn)
        => new()
        {
            new("role", turn.Role),
            new("ts", turn.Timestamp is { } timestamp ? PythonIso(timestamp) : null),
            new("sidechain", turn.Sidechain),
            new("text", turn.Text),
            new("tools", turn.Tools.Select(ToolObject).Cast<object?>().ToList()),
            new("errors", turn.Errors.Cast<object?>().ToList()),
        };

    private static List<KeyValuePair<string, object?>> ToolObject(ToolCall call)
        => new()
        {
            new("name", call.Name),
            new("input", call.Brief),
        };

    /// <summary>Python <c>datetime.isoformat()</c>: seconds, optional microseconds, UTC offset.</summary>
    internal static string PythonIso(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var micros = value.Ticks % TimeSpan.TicksPerSecond / 10;
        if (micros != 0) text += "." + micros.ToString("D6", CultureInfo.InvariantCulture);
        return text + Offset(value.Offset);
    }

    private static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return $"{sign}{absolute.Hours:D2}:{absolute.Minutes:D2}";
    }
}
