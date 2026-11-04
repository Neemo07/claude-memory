using System.IO;

namespace ClaudeMemory.App.Tests;

/// <summary>Locates the repository's frozen fixture corpus from the test output folder.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Fixtures => Path.Combine(RepoRoot, "tests", "fixtures");

    public static string FixtureRoot => Path.Combine(Fixtures, "fixture_root");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "ClaudeMemory.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
