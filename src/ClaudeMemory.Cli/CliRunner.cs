using System.Globalization;
using System.Text;
using ClaudeMemory.Core;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Rendering;
using ClaudeMemory.Core.Services;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Cli;

/// <summary>Everything the headless host needs from its environment, injected for tests.</summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Mirrors the reference's <c>HOME</c> (claude_memory.py:39).</summary>
    public string Home { get; init; } = MemoryOptions.DefaultHome();

    /// <summary>Used by <c>--here</c> (claude_memory.py:1035).</summary>
    public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>The single run instant handed to the renderers (claude_memory.py:1978).</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>argparse width: terminal columns minus two.</summary>
    public int HelpWidth { get; init; } = CliHelp.DetectWidth();

    public ShareClient ShareClient { get; init; } = new();
}

/// <summary>
/// Headless parity with <c>claude_memory.py main()</c> (claude_memory.py:1870-2027).
/// </summary>
/// <remarks>
/// The stderr lines, their order, and the exit codes (0 success, 1 no sessions, 2 usage or no
/// transcript dir, 3 share failure, 130 interrupt) match the reference. <c>Core</c> never
/// writes to a console, so every message is formatted here.
/// </remarks>
internal static class CliRunner
{
    /// <summary>Runs the command line and converts a cancellation into the reference's 130.</summary>
    public static int Run(string[] args, CliHost host)
    {
        try
        {
            return RunAsync(args, host).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            host.Error.Write("\n[claude-memory] interrupted\n");
            return 130;
        }
    }

    public static async Task<int> RunAsync(string[] args, CliHost host)
    {
        var specs = CliSpecs.Create(host.Now);
        var options = new MemoryOptions
        {
            Home = host.Home,
            Root = System.IO.Path.Combine(host.Home, ".claude"),
        };

        CliParseResult parsed;
        try
        {
            parsed = CliParser.Parse(args, specs, options);
        }
        catch (CliUsageException error)
        {
            host.Error.Write(CliHelp.Usage(specs, host.HelpWidth));
            host.Error.Write('\n');
            host.Error.Write($"{CliHelp.Prog}: error: {error.Message}\n");
            return 2;
        }

        if (parsed.Action == CliAction.Help)
        {
            host.Out.Write(CliHelp.Render(
                specs, System.IO.Path.Combine(host.Home, ".claude"), host.HelpWidth));
            return 0;
        }

        if (parsed.Action == CliAction.Version)
        {
            host.Out.Write($"{CliHelp.Prog} {BuildInfo.ReferenceVersion}\n");
            return 0;
        }

        try
        {
            return await ExecuteAsync(options, host).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The reference lets the OSError escape as a traceback; a raw stack trace is not
            // acceptable in this host, so it is reported the same way as other failures.
            Err(host, error.Message);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(MemoryOptions options, CliHost host)
    {
        options.PrintStdout = options.PrintStdout || options.Out is null;

        var shareSessions = await ImportSharesAsync(options, host).ConfigureAwait(false);
        if (shareSessions is null) return 3;

        var basePath = string.IsNullOrEmpty(options.ProjectsDir)
            ? System.IO.Path.Combine(options.Root, "projects")
            : options.ProjectsDir;

        var haveLocal = File.Exists(basePath) || Directory.Exists(basePath);
        if (!haveLocal && shareSessions.Count == 0)
        {
            Err(host, $"no transcript dir at {Display(basePath)}");
            Err(host, "run a Claude Code session in a project, pass --projects-dir, "
                      + "or import a share link with --share.");
            return 2;
        }

        var files = haveLocal
            ? SessionDiscovery.Discover(options.Root, options.ProjectsDir)
            : new List<string>();

        files = ApplyFastFilter(files, options, host);

        var sessions = new List<Session>();
        foreach (var file in files)
        {
            var session = JsonlSessionParser.ParseSession(file, options);
            if (session.Turns.Count > 0 || session.Summaries.Count > 0) sessions.Add(session);
        }

        if (options.ListOnly)
        {
            var listed = new List<Session>(sessions);
            listed.AddRange(shareSessions);
            host.Out.Write(ListRenderer.Build(listed, options));
            return 0;
        }

        var chosen = options.OnlyShare
            ? new List<Session>()
            : SessionSelector.Select(sessions, options, host.CurrentDirectory);
        chosen.AddRange(shareSessions);

        if (chosen.Count == 0)
        {
            Err(host, $"no sessions matched (searched {sessions.Count} of {files.Count}).");
            Err(host, "try widening --since / --cwd, or drop --sessions.");
            return 1;
        }

        chosen = SessionOrder.OldestFirst(chosen);

        var text = Render(chosen, options, host.Now);

        if (options.MaxChars < Budget.Floor
            && options.Mode is "memory" or "full"
            && !options.Quiet)
        {
            Err(host, $"--max-chars {options.MaxChars} raised to floor {Budget.Floor}");
        }

        if (options.AsciiOnly) text = AsciiFold.ToAscii(text);

        if (options.Out is not null) WriteOutput(options, text, chosen.Count, host);

        if (options.PrintStdout)
        {
            host.Out.Write(text);
            if (!text.EndsWith('\n')) host.Out.Write('\n');
        }

        if (!options.Quiet && options.Out is not null)
        {
            if (options.Mode == "memory" && options.Format == "plain")
            {
                Err(host, "paste-ready block: re-run with --format claude-md, "
                          + "or use --print to copy it.");
            }
            else if (options.Mode == "memory")
            {
                Err(host, "file already carries CLAUDE.md paste markers; append it to CLAUDE.md "
                          + "(it re-imports cleanly).");
            }
        }

        return 0;
    }

    /// <summary>
    /// Imports the share payloads. Returns null when the reference would exit 3, so the caller
    /// can distinguish "no shares" from "share failed".
    /// </summary>
    private static async Task<List<Session>?> ImportSharesAsync(MemoryOptions options, CliHost host)
    {
        var imported = new List<Session>();
        if (options.Share.Length == 0 && options.ShareFile is null) return imported;

        try
        {
            if (options.ShareFile is not null)
            {
                imported.Add(SharePayloadParser.LoadFile(options.ShareFile, options.IncludeThinking));
                if (!options.Quiet)
                {
                    Err(host, $"imported share payload from {Display(options.ShareFile)}");
                }
            }

            if (options.Share.Length > 0)
            {
                if (!options.Quiet) host.ShareClient.RetryNotice = message => Err(host, message);

                var payload = await host.ShareClient
                    .FetchAsync(options.Share, host.CancellationToken)
                    .ConfigureAwait(false);

                imported.Add(SharePayloadParser.Parse(payload, includeThinking: options.IncludeThinking));

                if (!options.Quiet)
                {
                    Err(host, $"imported share link ({MessageCount(payload)} messages)");
                }

                if (options.SourceNote.Length == 0)
                {
                    options.SourceNote =
                        $"claude.ai share link ({ShareClient.ShareUuid(options.Share)})";
                }

                if (options.ShareSave is not null)
                {
                    // Best-effort cache (IMPLEMENTATION_PLAN.md 2.1 item 8): a write failure
                    // never discards the payload that is already in memory.
                    if (ShareCache.TryWrite(options.ShareSave, payload, host.CancellationToken)
                        && !options.Quiet)
                    {
                        Err(host, $"cached payload -> {Display(options.ShareSave)}");
                    }
                }
            }
        }
        catch (ShareException error)
        {
            Err(host, error.Message);
            return null;
        }

        return imported;
    }

    private static List<string> ApplyFastFilter(
        List<string> files, MemoryOptions options, CliHost host)
    {
        if (!options.Fast || options.Since is not { } cutoff || files.Count == 0) return files;

        var kept = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (LastWrite(file) >= cutoff) kept.Add(file);
            }
            catch (IOException)
            {
                kept.Add(file);
            }
            catch (UnauthorizedAccessException)
            {
                kept.Add(file);
            }
        }

        var skipped = files.Count - kept.Count;
        if (!options.Quiet && skipped > 0)
        {
            Err(host, $"--fast: skipped {skipped} transcript(s) older than "
                      + $"{cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
                      + "by file mtime (approximate)");
        }

        return kept;
    }

    private static string Render(
        IReadOnlyList<Session> sessions, MemoryOptions options, DateTimeOffset now)
        => options.Mode switch
        {
            "transcript" => TranscriptBuilder.Build(sessions, options, now),
            "full" => FullMemoryBuilder.Build(sessions, options, now),
            "json" => JsonExporter.Build(sessions, now),
            _ => SummaryBuilder.Build(sessions, options, now),
        };

    private static void WriteOutput(
        MemoryOptions options, string text, int sessionCount, CliHost host)
    {
        var outPath = options.Out!;

        if (options.Mode == "memory" && options.History > 0 && File.Exists(outPath))
        {
            string? previous = null;
            try
            {
                previous = File.ReadAllText(
                    outPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
            }
            catch (IOException)
            {
                previous = null;
            }
            catch (UnauthorizedAccessException)
            {
                previous = null;
            }

            HistoryArchiver.Archive(outPath, previous, options.History, host.Now);
        }

        AtomicFileWriter.Write(outPath, text, host.CancellationToken);

        if (!options.Quiet)
        {
            Err(host, $"wrote {Display(outPath)} ({RuneCount(text)} chars, "
                      + $"{sessionCount} session(s), {TextUtil.SplitLines(text).Count} lines)");
        }
    }

    /// <summary>Python <c>len(text)</c> counts code points, not UTF-16 code units.</summary>
    private static int RuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    private static DateTimeOffset LastWrite(string path)
        => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

    private static int MessageCount(System.Text.Json.JsonElement payload)
        => payload.ValueKind == System.Text.Json.JsonValueKind.Object
           && payload.TryGetProperty("chat_messages", out var messages)
           && messages.ValueKind == System.Text.Json.JsonValueKind.Array
            ? messages.GetArrayLength()
            : 0;

    /// <summary>Python <c>str(Path)</c>: display separators are native.</summary>
    private static string Display(string path)
        => path.Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static void Err(CliHost host, string message)
        => host.Error.Write("[claude-memory] " + message + "\n");
}