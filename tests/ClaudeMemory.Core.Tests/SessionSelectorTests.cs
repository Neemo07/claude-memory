using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// The session list is built explicitly here rather than discovered, so the expectations do
/// not depend on fixture file timestamps.
/// </summary>
public sealed class SessionSelectorTests
{
    private const string AuditSession = "11111111-2222-3333-4444-555555555555";
    private const string SinceSession = "99999999-8888-7777-6666-555555555555";
    private const string SubagentSession = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static List<Session> FixtureSessions()
    {
        var projects = Path.Combine(TestPaths.FixtureRoot, "projects");
        var paths = new[]
        {
            Path.Combine(projects, "-C-testproj", AuditSession + ".jsonl"),
            Path.Combine(projects, "-C-testproj", SinceSession + ".jsonl"),
            Path.Combine(projects, "-C-alpha-srv", SubagentSession + ".jsonl"),
            Path.Combine(projects, "-C-testproj", "corrupt.jsonl"),
            Path.Combine(projects, "-C-work-demo", "synthetic.jsonl"),
            Path.Combine(projects, "-C-work-arabic", "arabic.jsonl"),
        };

        return paths.Select(path => JsonlSessionParser.ParseSession(path, new MemoryOptions())).ToList();
    }

    private static List<string> Select(MemoryOptions opts, string currentDirectory = "")
        => SessionSelector.Select(FixtureSessions(), opts, currentDirectory).Select(session => session.Sid).ToList();

    [Fact]
    public void Select_WithNoFiltersKeepsEverySession()
        => Assert.Equal(
            new[] { AuditSession, SinceSession, SubagentSession, "corrupt", "synth-0001", "arabic-0001" },
            Select(new MemoryOptions()));

    [Fact]
    public void Select_KeepsTheNewestCount()
        => Assert.Equal(
            new[] { "synth-0001", "arabic-0001" },
            Select(new MemoryOptions { Sessions = 2 }));

    [Theory]
    [InlineData("C:/testproj")]
    [InlineData(@"C:\testproj")]
    public void Select_MatchesTheWorkingDirectoryRegardlessOfSeparators(string cwd)
        => Assert.Equal(new[] { AuditSession, SinceSession }, Select(new MemoryOptions { Cwd = cwd }));

    [Fact]
    public void Select_HereUsesTheSuppliedDirectory()
        => Assert.Equal(
            new[] { AuditSession, SinceSession },
            Select(new MemoryOptions { Here = true }, "C:/testproj"));

    [Fact]
    public void Select_HereWithAnUnrelatedDirectoryMatchesNothing()
        => Assert.Empty(Select(new MemoryOptions { Here = true }, "C:/nowhere"));

    [Fact]
    public void Select_FiltersByBranch()
    {
        Assert.Equal(
            new[] { "arabic-0001" },
            Select(new MemoryOptions { Branch = "main", Sessions = 1 }));

        Assert.Empty(Select(new MemoryOptions { Branch = "nope" }));
    }

    [Fact]
    public void Select_KeepsSessionsAtOrAfterSinceAndUnknownTimestamps()
        => Assert.Equal(
            new[] { AuditSession, SinceSession, SubagentSession, "corrupt" },
            Select(new MemoryOptions { Since = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero) }));

    [Fact]
    public void Select_SinceComparesAgainstTheSessionEnd()
    {
        // The alpha session runs 09:00-09:09.
        Assert.Equal(
            new[] { SubagentSession },
            Select(new MemoryOptions
            {
                Cwd = "C:/alpha/srv",
                Since = new DateTimeOffset(2026, 10, 5, 9, 5, 0, TimeSpan.Zero),
            }));

        Assert.Empty(Select(new MemoryOptions
        {
            Cwd = "C:/alpha/srv",
            Since = new DateTimeOffset(2026, 10, 5, 9, 10, 0, TimeSpan.Zero),
        }));
    }
}