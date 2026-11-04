using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// Save-path refusal rules and the default output folder, ported from gui.py:39, 886-892 and
/// 1076-1088 (<c>ALLOWED_SUFFIX</c>, <c>default_out_dir</c>, <c>safe_out_path</c>).
/// </summary>
public static class PathPolicy
{
    /// <summary>Extensions a save target may use (gui.py:39).</summary>
    public static readonly string[] AllowedSuffixes = { ".md", ".markdown", ".json", ".txt" };

    /// <summary>Name used when the caller supplies none (gui.py:1078).</summary>
    public const string DefaultName = "MEMORY.md";

    private static readonly Regex PercentVariable = new("%([^%]+)%", RegexOptions.Compiled);

    private static readonly Regex DollarVariable = new(@"\$(\w+|\{[^}]+\})", RegexOptions.Compiled);

    /// <summary>
    /// Resolves the save target, refusing anything that is not a plain data file
    /// (gui.py:1076). The folder must be absolute and the name must be a single file name
    /// with an allowed suffix.
    /// </summary>
    /// <exception cref="SaveException">The folder or name violates a save rule.</exception>
    public static string ResolveSavePath(string? name, string? folder, string defaultDirectory)
    {
        var fileName = (name ?? DefaultName).Trim();
        if (fileName.Length == 0) fileName = DefaultName;

        var directory = (folder ?? "").Trim();
        var baseDirectory = directory.Length > 0 ? ExpandVariables(directory) : defaultDirectory;
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new SaveException(
                "The folder must be an absolute path, e.g. C:\\Users\\you\\Documents");
        }

        if (fileName != Path.GetFileName(fileName) || fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new SaveException("The file name must not contain a path.");
        }

        if (!HasAllowedSuffix(fileName))
        {
            throw new SaveException("The file name must end with " + string.Join(", ", AllowedSuffixes));
        }

        return Path.Combine(baseDirectory, fileName);
    }

    /// <summary>True when the path ends with an allowed save suffix (gui.py:1086).</summary>
    public static bool HasAllowedSuffix(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var suffix in AllowedSuffixes)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Appends <c>.md</c> when a Save As path uses an unsupported extension (gui.py:1195).</summary>
    public static string WithAllowedSuffix(string path)
        => HasAllowedSuffix(path) ? path : path + ".md";

    /// <summary>
    /// The folder a save defaults to: Documents under the user profile when it exists
    /// (gui.py:886).
    /// </summary>
    public static string DefaultOutputDirectory()
    {
        foreach (var variable in new[] { "USERPROFILE", "HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value)) continue;
            var documents = Path.Combine(value, "Documents");
            return Directory.Exists(documents) ? documents : value;
        }

        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Windows-flavoured <c>os.path.expandvars</c>: expands <c>%VAR%</c> and <c>$VAR</c> /
    /// <c>${VAR}</c>. Unknown variables are left untouched, as the reference does.
    /// </summary>
    public static string ExpandVariables(string path)
    {
        if (!path.Contains('%') && !path.Contains('$')) return path;

        var expanded = PercentVariable.Replace(
            path, match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
        return DollarVariable.Replace(expanded, match =>
        {
            var name = match.Groups[1].Value;
            if (name.StartsWith('{') && name.EndsWith('}')) name = name[1..^1];
            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }
}

/// <summary>A save request was refused by <see cref="PathPolicy"/> or <see cref="SaveService"/>.</summary>
public sealed class SaveException : Exception
{
    public SaveException(string message) : base(message)
    {
    }

    public SaveException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
