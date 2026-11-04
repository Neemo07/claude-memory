using System.Globalization;
using ClaudeMemory.Core.Model;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// Drives <see cref="CliRunner"/> in-process with captured stdio, a fixed clock, and an
/// explicit help width, so no test depends on the ambient terminal or the real user profile.
/// </summary>
internal static class CliHarness
{
    /// <summary>The instant every golden was generated at (control value, not wall clock).</summary>
    public static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-04T15:11:55Z", CultureInfo.InvariantCulture);

    /// <summary>The reference's <c>COLUMNS=80</c> gives argparse a width of 78.</summary>
    public const int HelpWidth = 78;

    public static int Run(
        string[] args,
        out string stdout,
        out string stderr,
        string? home = null,
        CancellationToken cancellationToken = default,
        string? currentDirectory = null,
        DateTimeOffset? now = null)
    {
        var outWriter = new StringWriter { NewLine = "\n" };
        var errWriter = new StringWriter { NewLine = "\n" };

        var host = new CliHost
        {
            Out = outWriter,
            Error = errWriter,
            Home = home ?? MemoryOptions.DefaultHome(),
            Now = now ?? Now,
            HelpWidth = HelpWidth,
            CurrentDirectory = currentDirectory ?? TestPaths.RepoRoot,
            CancellationToken = cancellationToken,
        };

        var code = CliRunner.Run(args, host);

        stdout = outWriter.ToString();
        stderr = errWriter.ToString();
        return code;
    }
}
