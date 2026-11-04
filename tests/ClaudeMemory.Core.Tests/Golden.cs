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

        return string.Join("\n", lines);
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
