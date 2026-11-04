using System.Globalization;

namespace ClaudeMemory.Core;

/// <summary>
/// Identity strings that appear in the generated memory (claude_memory.py:37-44).
/// </summary>
/// <remarks>
/// The reference writes its own name and version into the <c>_Generated ..._</c> line, so
/// the C# host keeps the same literals to stay byte-parity in every mode.
/// </remarks>
public static class BuildInfo
{
    /// <summary>The version stamped into generated output (claude_memory.py:37).</summary>
    public const string ReferenceVersion = "1.1.0";

    /// <summary>The generator name used in the <c>_Generated ..._</c> line.</summary>
    public const string GeneratorName = "claude_memory.py";

    /// <summary>Opening marker for <c>--format claude-md</c> (claude_memory.py:43).</summary>
    public const string BeginMarker = "<!-- claude-memory:begin -->";

    /// <summary>Closing marker for <c>--format claude-md</c> (claude_memory.py:44).</summary>
    public const string EndMarker = "<!-- claude-memory:end -->";

    /// <summary>
    /// Formats an instant the way <c>iso_now()</c> does: UTC, second precision, trailing
    /// <c>Z</c> (claude_memory.py:351).
    /// </summary>
    public static string IsoNow(DateTimeOffset now)
        => now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
