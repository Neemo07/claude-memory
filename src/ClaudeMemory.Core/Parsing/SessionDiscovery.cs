namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Finds transcript files, ported from claude_memory.py:1020-1028 (<c>discover</c>).
/// </summary>
public static class SessionDiscovery
{
    /// <summary>
    /// Lists non-empty <c>*.jsonl</c> files under <paramref name="projectsDir"/> (or
    /// <c>&lt;root&gt;/projects</c>), oldest first, ties broken by file name.
    /// </summary>
    public static List<string> Discover(string root, string? projectsDir)
    {
        var baseDir = string.IsNullOrEmpty(projectsDir) ? System.IO.Path.Combine(root, "projects") : projectsDir;

        var files = new List<string>();
        if (Directory.Exists(baseDir))
        {
            foreach (var path in Directory.EnumerateFiles(baseDir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(path).Length > 0) files.Add(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return files
            .OrderBy(LastWriteUtc)
            .ThenBy(System.IO.Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Last write time, or <see cref="DateTime.MinValue"/> when it cannot be read.</summary>
    private static DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
