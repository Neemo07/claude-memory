using System.Diagnostics;
using System.Text;

namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// Spawns the real CLI executable so the stdio contract (<c>Program.Main</c>: UTF-8 without a
/// BOM, LF newlines) is exercised end to end, not just through a <see cref="StringWriter"/>.
/// </summary>
internal static class CliProcess
{
    /// <summary>
    /// Absolute path of the CLI assembly built by the project reference, in the same
    /// configuration as the running test assembly.
    /// </summary>
    public static string DllPath()
    {
        var tfmDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var configuration = new DirectoryInfo(tfmDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(
            TestPaths.RepoRoot, "src", "ClaudeMemory.Cli", "bin", configuration, "net8.0",
            "ClaudeMemory.Cli.dll");
    }

    public static (int Code, byte[] Stdout, string Stderr) Run(params string[] args)
    {
        var dll = DllPath();
        Assert.True(File.Exists(dll), "CLI assembly not built: " + dll);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(dll);
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        // Deterministic help width and no cp1252 surprises, matching the reference capture.
        startInfo.Environment["COLUMNS"] = "80";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet'.");

        using var stdout = new MemoryStream();
        var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var readErr = process.StandardError.ReadToEndAsync();

        process.WaitForExit();
        copyOut.GetAwaiter().GetResult();
        var stderr = readErr.GetAwaiter().GetResult();

        return (process.ExitCode, stdout.ToArray(), stderr);
    }

    public static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
}
