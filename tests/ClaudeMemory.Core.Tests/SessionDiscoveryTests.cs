using ClaudeMemory.Core.Parsing;

namespace ClaudeMemory.Core.Tests;

public sealed class SessionDiscoveryTests
{
    private static readonly string[] FixtureNames =
    {
        "11111111-2222-3333-4444-555555555555.jsonl",
        "99999999-8888-7777-6666-555555555555.jsonl",
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl",
        "corrupt.jsonl",
        "synthetic.jsonl",
        "arabic.jsonl",
    };

    [Fact]
    public void Discover_FindsEveryNonEmptyTranscriptUnderProjects()
    {
        var files = SessionDiscovery.Discover(TestPaths.FixtureRoot, null);

        Assert.Equal(
            FixtureNames.OrderBy(name => name, StringComparer.Ordinal),
            files.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Discover_HonoursAnExplicitProjectsDirectory()
    {
        var projectsDir = Path.Combine(TestPaths.FixtureRoot, "projects");

        Assert.Equal(
            SessionDiscovery.Discover(TestPaths.FixtureRoot, null),
            SessionDiscovery.Discover(TestPaths.FixtureRoot, projectsDir));
    }

    [Fact]
    public void Discover_ReturnsNothingWhenTheProjectsDirectoryIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cm-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(SessionDiscovery.Discover(missing, null));
    }

    [Fact]
    public void Discover_SortsByWriteTimeThenFileName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cm-disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var oldest = Path.Combine(directory, "b.jsonl");
            var middle = Path.Combine(directory, "a.jsonl");
            var tiedFirst = Path.Combine(directory, "c.jsonl");
            var tiedSecond = Path.Combine(directory, "d.jsonl");
            var skipped = Path.Combine(directory, "empty.jsonl");

            foreach (var path in new[] { oldest, middle, tiedFirst, tiedSecond, skipped })
                File.WriteAllText(path, "{}\n");

            File.WriteAllText(skipped, "");
            File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(middle, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(tiedFirst, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(tiedSecond, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

            var files = SessionDiscovery.Discover(directory, directory);

            Assert.Equal(new[] { "b.jsonl", "a.jsonl", "c.jsonl", "d.jsonl" }, files.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}