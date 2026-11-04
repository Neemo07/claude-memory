using System.Text;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// Temp-then-replace write, ported from claude_memory.py:1708-1712 (<c>atomic_write</c>).
/// </summary>
/// <remarks>
/// The reference writes UTF-8 without a BOM and with LF newlines, then replaces the target.
/// A cancellation before the replace removes the temporary file so a cancelled save leaves no
/// stray <c>&lt;name&gt;.tmp</c> behind.
/// </remarks>
public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = full + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(temporary, text, Utf8NoBom);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
