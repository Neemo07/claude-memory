using System.Text;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// Save orchestration, ported from gui.py:1091-1103 and 1176-1205 (<c>do_save</c>,
/// <c>do_save_as</c>) and the history rule in claude_memory.py:1995-2001.
/// </summary>
/// <remarks>
/// <c>memory</c> mode keeps a versioned history of the previous file; the other modes overwrite
/// without archiving. The caller passes <c>history: 0</c> for those modes.
/// </remarks>
public static class SaveService
{
    /// <summary>Writes to the configured folder after refusing an unsafe name or folder.</summary>
    public static string Save(string? name, string? folder, string text, int history,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new SaveException("There is nothing to save yet.");

        var target = PathPolicy.ResolveSavePath(name, folder, PathPolicy.DefaultOutputDirectory());
        return Write(target, text, history, now, cancellationToken);
    }

    /// <summary>Writes to a user-picked path, appending <c>.md</c> when the suffix is unsupported.</summary>
    public static string SaveAs(string chosenPath, string text, int history,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new SaveException("There is nothing to save yet.");

        var target = PathPolicy.WithAllowedSuffix(PathPolicy.ExpandVariables(chosenPath));
        return Write(target, text, history, now, cancellationToken);
    }

    /// <summary>Creates the containing folder and returns it, for a host folder-reveal action.</summary>
    public static string EnsureContainingFolder(string target)
    {
        var full = Path.GetFullPath(target);
        var folder = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Write(string target, string text, int history, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (history > 0 && File.Exists(target))
        {
            string? previous = null;
            try
            {
                // utf-8 with replacement fallback, matching read_text(encoding="utf-8", errors="replace")
                previous = Encoding.UTF8.GetString(File.ReadAllBytes(target));
            }
            catch (IOException)
            {
                previous = null;
            }
            catch (UnauthorizedAccessException)
            {
                previous = null;
            }

            try
            {
                HistoryArchiver.Archive(target, previous, history, now);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        AtomicFileWriter.Write(target, text, cancellationToken);
        return target;
    }
}
