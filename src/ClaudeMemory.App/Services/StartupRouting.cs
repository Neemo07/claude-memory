using System.IO;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.App.Services;

/// <summary>What the first window should do after it appears (PRD FR-23, §9.25).</summary>
public enum StartupAction
{
    ShowWindow,
    OpenFile,
}

/// <summary>One parsed command line.</summary>
public sealed class StartupRoute
{
    public required StartupAction Action { get; init; }

    /// <summary>The file to preview, when <see cref="Action"/> is <see cref="StartupAction.OpenFile"/>.</summary>
    public string Path { get; init; } = "";

    /// <summary>A status-line message for an unusable argument, or null.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Command-line routing: <c>claude-memory [file.md]</c>. A missing path, a folder, or a
/// non-Markdown suffix still opens the window and explains itself instead of failing
/// (PRD §9.25). Only the first argument is considered.
/// </summary>
public static class StartupRouting
{
    private static readonly string[] PreviewSuffixes = { ".md", ".markdown" };

    public static StartupRoute Parse(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new StartupRoute { Action = StartupAction.ShowWindow };
        }

        var raw = arguments[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new StartupRoute { Action = StartupAction.ShowWindow };
        }

        var path = ExpandUser(PathPolicy.ExpandVariables(raw));

        if (Directory.Exists(path))
        {
            return new StartupRoute
            {
                Action = StartupAction.ShowWindow,
                Message = "cannot open \"" + path + "\": that is a folder",
            };
        }

        if (!File.Exists(path))
        {
            return new StartupRoute
            {
                Action = StartupAction.ShowWindow,
                Message = "cannot open \"" + path + "\": no such file",
            };
        }

        if (!HasPreviewSuffix(path))
        {
            return new StartupRoute
            {
                Action = StartupAction.ShowWindow,
                Message = "cannot open \"" + path + "\": choose a Markdown file (.md or .markdown)",
            };
        }

        return new StartupRoute { Action = StartupAction.OpenFile, Path = path };
    }

    private static bool HasPreviewSuffix(string path)
    {
        var suffix = Path.GetExtension(path);
        foreach (var allowed in PreviewSuffixes)
        {
            if (string.Equals(suffix, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Python <c>Path.expanduser()</c> for a leading <c>~</c> (gui.py:1029).</summary>
    private static string ExpandUser(string path)
    {
        if (path.Length == 0 || path[0] != '~') return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;
        if (path[1] is '/' or '\\') return Path.Combine(home, path[2..]);
        return path;
    }
}
