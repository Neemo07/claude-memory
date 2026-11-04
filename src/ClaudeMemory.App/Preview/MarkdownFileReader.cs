using System.IO;
using System.Text;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.App.Preview;

/// <summary>One local Markdown file that passed the preview guards.</summary>
public sealed class PreviewFile
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// Reads one local Markdown file for the preview, ported from gui.py:1027-1049
/// (<c>read_markdown_file</c>).
/// </summary>
/// <remarks>
/// The order of the guards and the exact wording matter: PRD §9.15 wants a clear rejection
/// for a non-Markdown file, a file over 4 MiB, an empty file, and a file containing NUL
/// bytes. <c>expandvars</c> and <c>~</c> expansion match the reference; the path is
/// deliberately <em>not</em> made absolute, so the messages show the path the user typed.
/// </remarks>
public static class MarkdownFileReader
{
    /// <summary>Preview size limit, 4 MiB (gui.py:42).</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    private static readonly string[] AllowedSuffixes = { ".md", ".markdown" };

    /// <exception cref="PreviewException">The path is missing, too large, not text, or empty.</exception>
    public static PreviewFile Read(string path)
    {
        var target = ExpandUser(PathPolicy.ExpandVariables(path ?? ""));

        if (!File.Exists(target))
        {
            throw new PreviewException($"No such file: {target}");
        }

        if (!HasAllowedSuffix(target))
        {
            throw new PreviewException("Choose a Markdown file (.md or .markdown).");
        }

        long size;
        try
        {
            size = new FileInfo(target).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PreviewException($"Cannot read {target}: {error.Message}");
        }

        if (size > MaxBytes)
        {
            throw new PreviewException(
                $"{Path.GetFileName(target)} is too large to preview "
                + $"({size} bytes; the limit is {MaxBytes}).");
        }

        string text;
        try
        {
            // detectEncodingFromByteOrderMarks strips a UTF-8 BOM, matching utf-8-sig;
            // throwOnInvalidBytes: false replaces undecodable bytes, matching errors="replace".
            using var reader = new StreamReader(
                target,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PreviewException($"Cannot read {target}: {error.Message}");
        }

        if (text.Contains('\0'))
        {
            throw new PreviewException($"{Path.GetFileName(target)} is not a text file.");
        }

        if (text.Trim().Length == 0)
        {
            throw new PreviewException($"{Path.GetFileName(target)} is empty.");
        }

        return new PreviewFile
        {
            Path = target,
            Name = Path.GetFileName(target),
            Text = text,
        };
    }

    /// <summary>Lower-case suffix check; a file with no dot is rejected (gui.py:1032).</summary>
    private static bool HasAllowedSuffix(string path)
    {
        var suffix = Path.GetExtension(path);
        foreach (var allowed in AllowedSuffixes)
        {
            if (string.Equals(suffix, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Python <c>Path.expanduser()</c>: only a leading <c>~</c> is substituted.</summary>
    private static string ExpandUser(string path)
    {
        if (path.Length == 0 || path[0] != '~') return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;

        if (path[1] is '/' or '\\') return Path.Combine(home, path[2..]);
        return path;
    }
}
