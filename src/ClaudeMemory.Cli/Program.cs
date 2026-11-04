using System.Text;

namespace ClaudeMemory.Cli;

/// <summary>
/// Executable entry point. Reproduces the reference's stdio contract
/// (claude_memory.py:328-346 <c>configure_stdio</c>): UTF-8 without a BOM, LF newlines, and
/// the interrupt message on Ctrl+C.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n" };

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            // Let the run unwind so the share fetch and the atomic write can stop cleanly.
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var host = new CliHost
            {
                Out = stdout,
                Error = stderr,
                CancellationToken = cancellation.Token,
            };

            return CliRunner.Run(args, host);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            stdout.Flush();
            stderr.Flush();
        }
    }
}