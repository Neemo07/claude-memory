using System.Globalization;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Rendering;

namespace ClaudeMemory.App.ViewModels;

/// <summary>
/// One row of the "found on this machine" table. The columns match the reference table
/// (gui.py:317, 478): when, folder, turns. The file name is the row tooltip.
/// </summary>
public sealed class SessionRow
{
    public required string File { get; init; }

    public required string When { get; init; }

    public required string Folder { get; init; }

    public int Turns { get; init; }

    /// <summary>Builds the display row from a parsed session (gui.py:942-950).</summary>
    public static SessionRow From(Session session, MemoryOptions options) => new()
    {
        File = System.IO.Path.GetFileName(session.Path),
        When = session.Start is { } start
            ? start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "",
        Folder = SummaryBuilder.SessionWhere(session, options),
        Turns = session.Turns.Count,
    };
}