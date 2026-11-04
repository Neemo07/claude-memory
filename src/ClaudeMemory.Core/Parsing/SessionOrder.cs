using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Core.Parsing;

/// <summary>
/// Session ordering, ported from claude_memory.py:1977-1979.
/// </summary>
/// <remarks>
/// The reference sorts <c>chosen</c> in place with <c>key=s.start or datetime.min</c> before
/// rendering, so the sessions table and the recency-based sections agree. Python's sort is
/// stable and <see cref="Enumerable.OrderBy"/> is too, so ties keep discovery order.
/// </remarks>
public static class SessionOrder
{
    /// <summary>Returns the sessions oldest-first; a missing start sorts before every real one.</summary>
    public static List<Session> OldestFirst(IEnumerable<Session> sessions)
        => sessions.OrderBy(session => session.Start ?? DateTimeOffset.MinValue).ToList();
}
