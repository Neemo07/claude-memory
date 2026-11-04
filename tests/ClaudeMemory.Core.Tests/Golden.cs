using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// The four normalisations IMPLEMENTATION_PLAN.md §2 allows in golden comparison: the
/// <c>_Generated ..._</c> line, the JSON <c>tool</c> field, fixture-root paths, and line endings.
/// Only the ones that apply to a memory document are used here.
/// </summary>
internal static class Golden
{
    private const string GeneratedPlaceholder = "_Generated <normalised>_";

    private static readonly Regex GeneratedLine = new(@"^_Generated .*_$", RegexOptions.Compiled);

    // The reference stamps the run time into every mode. The transcript and JSON modes do not
    // put it on a "_Generated ..._" line, so any bare UTC instant is normalised too.
    private static readonly Regex IsoUtcInstant = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", RegexOptions.Compiled);

    private static readonly Regex JsonGenerated = new("\"generated\": \"[^\"]*\"", RegexOptions.Compiled);

    private static readonly Regex JsonTool = new("\"tool\": \"[^\"]*\"", RegexOptions.Compiled);

    public static string Normalise(string text)
    {
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var root = TestPaths.FixtureRoot;
        foreach (var variant in new[] { root, root.Replace('\\', '/') })
        {
            lf = lf.Replace(variant, "<root>", StringComparison.OrdinalIgnoreCase);
        }

        var lines = lf.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (GeneratedLine.IsMatch(lines[index])) lines[index] = GeneratedPlaceholder;
        }

        return IsoUtcInstant.Replace(string.Join("\n", lines), "<time>");
    }

    /// <summary>
    /// Normalises a JSON document: the fixture root (in raw and backslash-escaped form) plus the
    /// two volatile identity fields, <c>generated</c> and <c>tool</c> (IMPLEMENTATION_PLAN.md section 2).
    /// The layout and every other value still have to match exactly.
    /// </summary>
    public static string NormaliseJson(string text)
    {
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var root = TestPaths.FixtureRoot;
        foreach (var variant in new[] { root, root.Replace('\\', '/'), root.Replace("\\", "\\\\") })
        {
            lf = lf.Replace(variant, "<root>", StringComparison.OrdinalIgnoreCase);
        }

        lf = JsonGenerated.Replace(lf, "\"generated\": \"<normalised>\"");
        return JsonTool.Replace(lf, "\"tool\": \"<normalised>\"");
    }

    /// <summary>Builds a readable first-difference report for a failing parity assertion.</summary>
    public static string Diff(string name, string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var builder = new StringBuilder();
        builder.Append(name).Append(": output differs from the frozen reference.\n");

        var reported = 0;
        var total = Math.Max(expectedLines.Length, actualLines.Length);
        for (var index = 0; index < total && reported < 40; index++)
        {
            var left = index < expectedLines.Length ? expectedLines[index] : "<missing>";
            var right = index < actualLines.Length ? actualLines[index] : "<missing>";
            if (left == right) continue;

            builder.Append("line ").Append(index + 1).Append(":\n");
            builder.Append("  golden: ").Append(left).Append('\n');
            builder.Append("  actual: ").Append(right).Append('\n');
            reported++;
        }

        if (reported == 0) builder.Append("(no line differs; trailing length mismatch only)\n");
        return builder.ToString();
    }
}
