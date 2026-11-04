using System.Text.Json;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// Saves a fetched share payload so a failed or rate-limited fetch can be replayed offline,
/// ported from the <c>--share-save</c> branch of claude_memory.py:1929-1933.
/// </summary>
/// <remarks>
/// The cache is written with <c>json.dumps(payload, indent=1, ensure_ascii=False) + "\n"</c>.
/// Unlike the reference, a write failure does not abort the import: Phase 4 of the plan makes
/// the cache best-effort, because the payload is already in memory and the user still gets
/// their memory document.
/// </remarks>
public static class ShareCache
{
    /// <summary>Writes the payload; returns false when the destination cannot be written.</summary>
    public static bool TryWrite(string path, JsonElement payload, CancellationToken cancellationToken = default)
    {
        try
        {
            AtomicFileWriter.Write(path, PythonJsonWriter.Dump(payload, indent: 1) + "\n", cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}