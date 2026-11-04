using System.Text;

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
    /// Faithful port of <c>ntpath.expandvars</c>: expands <c>$var</c>, <c>${var}</c> and
    /// <c>%var%</c>, leaves unknown variables unchanged, treats <c>$$</c> and <c>%%</c> as a
    /// literal <c>$</c>/<c>%</c>, and expands nothing inside single quotes.
    /// </summary>
    public static string ExpandVariables(string path)
    {
        if (!path.Contains('$') && !path.Contains('%')) return path;

        var result = new StringBuilder();
        var index = 0;
        while (index < path.Length)
        {
            var current = path[index];
            if (current == '\'')
            {
                // No expansion within single quotes.
                path = path[(index + 1)..];
                var close = path.IndexOf('\'');
                if (close < 0)
                {
                    result.Append('\'').Append(path);
                    index = path.Length - 1;
                }
                else
                {
                    result.Append('\'').Append(path, 0, close + 1);
                    index = close;
                }
            }
            else if (current == '%')
            {
                if (index + 1 < path.Length && path[index + 1] == '%')
                {
                    result.Append('%');
                    index += 1;
                }
                else
                {
                    path = path[(index + 1)..];
                    var close = path.IndexOf('%');
                    if (close < 0)
                    {
                        result.Append('%').Append(path);
                        index = path.Length - 1;
                    }
                    else
                    {
                        var name = path[..close];
                        result.Append(Environment.GetEnvironmentVariable(name) ?? ("%" + name + "%"));
                        index = close;
                    }
                }
            }
            else if (current == '$')
            {
                if (index + 1 < path.Length && path[index + 1] == '$')
                {
                    result.Append('$');
                    index += 1;
                }
                else if (index + 1 < path.Length && path[index + 1] == '{')
                {
                    path = path[(index + 2)..];
                    var close = path.IndexOf('}');
                    if (close < 0)
                    {
                        result.Append("${").Append(path);
                        index = path.Length - 1;
                    }
                    else
                    {
                        var name = path[..close];
                        result.Append(Environment.GetEnvironmentVariable(name) ?? ("${" + name + "}"));
                        index = close;
                    }
                }
                else
                {
                    var name = new StringBuilder();
                    index += 1;
                    while (index < path.Length && IsVariableChar(path[index]))
                    {
                        name.Append(path[index]);
                        index += 1;
                    }

                    var variable = name.ToString();
                    result.Append(Environment.GetEnvironmentVariable(variable) ?? ("$" + variable));
                    if (index < path.Length) index -= 1;
                }
            }
            else
            {
                result.Append(current);
            }

            index += 1;
        }

        return result.ToString();
    }

    /// <summary><c>string.ascii_letters + string.digits + '_-'</c> (ntpath.expandvars).</summary>
    private static bool IsVariableChar(char value)
        => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
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
