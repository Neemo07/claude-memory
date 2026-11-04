using System.IO;
using ClaudeMemory.App.Services;

namespace ClaudeMemory.App.Tests;

public sealed class SessionScannerTests
{
    [Fact]
    public void Loads_the_fixture_rows()
    {
        var scanner = new SessionScanner(home: TestPaths.RepoRoot, root: TestPaths.FixtureRoot);

        var result = scanner.Scan();

        Assert.Equal(TestPaths.FixtureRoot, result.Root);
        Assert.Equal(Path.Combine(TestPaths.FixtureRoot, "projects"), result.ProjectsDirectory);
        Assert.NotEmpty(result.Sessions);
        Assert.All(result.Sessions, row =>
        {
            Assert.EndsWith(".jsonl", row.File);
            Assert.False(string.IsNullOrWhiteSpace(row.Folder));
            Assert.True(row.Turns > 0);
        });
        Assert.False(string.IsNullOrWhiteSpace(result.DefaultOutputDirectory));
    }

    [Fact]
    public void Missing_projects_directory_is_empty_and_still_reports_the_root()
    {
        var root = Path.Combine(TestPaths.RepoRoot, "no-such-root");
        var scanner = new SessionScanner(home: TestPaths.RepoRoot, root: root);

        var result = scanner.Scan();

        Assert.Empty(result.Sessions);
        Assert.Equal(root, result.Root);
        Assert.False(string.IsNullOrWhiteSpace(result.DefaultOutputDirectory));
    }
}