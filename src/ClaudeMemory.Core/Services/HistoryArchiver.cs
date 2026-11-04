using System.Globalization;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// Versioned history for the <c>memory</c> mode, ported from claude_memory.py:1715-1731
/// (<c>archive_history</c>).
/// </summary>
/// <remarks>
/// The previous content is written to <c>&lt;name&gt;.history/&lt;stamp&gt;-&lt;n&gt;.md</c> after
/// deleting the oldest archived files until fewer than <c>keep</c> remain. Only files inside
/// that folder are ever touched.
/// </remarks>
public static class HistoryArchiver
{
    public static void Archive(string outPath, string? previous, int keep, DateTimeOffset now)
    {
        if (keep <= 0) return;

        var full = Path.GetFullPath(outPath);
        var directory = Path.GetDirectoryName(full) ?? ".";
        var historyDirectory = Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + ".history");
        Directory.CreateDirectory(historyDirectory);

        var existing = Directory.GetFiles(historyDirectory, "*.md")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        while (existing.Count >= keep)
        {
            var oldest = existing[0];
            existing.RemoveAt(0);
            try
            {
                File.Delete(oldest);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var stamp = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var index = 1;
        while (File.Exists(Path.Combine(historyDirectory, $"{stamp}-{index}.md"))) index++;

        AtomicFileWriter.Write(Path.Combine(historyDirectory, $"{stamp}-{index}.md"), previous ?? "");
    }
}
