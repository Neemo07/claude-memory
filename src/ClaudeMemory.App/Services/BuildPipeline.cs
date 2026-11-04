using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Rendering;
using ClaudeMemory.Core.Services;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.App.Services;

/// <summary>
/// The desktop Generate pipeline, ported from <c>gui.py do_build</c> (gui.py:955-1014) and
/// <c>build_options</c> (gui.py:904-930). It runs the same Core stages the CLI runner does, so
/// the window and the headless host produce identical documents.
/// </summary>
/// <remarks>
/// Unlike the CLI, failures are raised as <see cref="BuildException"/> carrying the reference's
/// user-facing message instead of printing to a console; the shell shows it in the status line.
/// The status line uses the reference stats string; <c>len(text)</c> counts code points, so the
/// character count enumerates runes.
/// </remarks>
public sealed class BuildPipeline : IBuildPipeline
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _projectsDir;
    private readonly string _currentDirectory;
    private readonly ShareClient _shareClient;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _defaultOutputDirectory;

    public BuildPipeline(
        string? home = null,
        string? root = null,
        string? projectsDir = null,
        string? currentDirectory = null,
        ShareClient? shareClient = null,
        Func<DateTimeOffset>? now = null,
        string? defaultOutputDirectory = null,
        Action<string>? retryNotice = null)
    {
        _home = home ?? MemoryOptions.DefaultHome();
        _root = root ?? System.IO.Path.Combine(_home, ".claude");
        _projectsDir = projectsDir;
        _currentDirectory = currentDirectory ?? Environment.CurrentDirectory;
        _shareClient = shareClient ?? new ShareClient();
        _shareClient.RetryNotice = retryNotice;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _defaultOutputDirectory = defaultOutputDirectory ?? PathPolicy.DefaultOutputDirectory();
    }

    public async Task<BuildResult> BuildAsync(
        BuildRequest request, CancellationToken cancellationToken = default)
    {
        var now = _now();
        var options = BuildOptions(request, _home, _root, now);

        var shareSessions = await ImportSharesAsync(request, options, cancellationToken)
            .ConfigureAwait(false);

        var local = new List<Session>();
        if (request.Source is MemorySource.Local or MemorySource.Both)
        {
            var basePath = _projectsDir ?? System.IO.Path.Combine(options.Root, "projects");
            if (File.Exists(basePath) || Directory.Exists(basePath))
            {
                var parsed = new List<Session>();
                foreach (var path in SessionDiscovery.Discover(options.Root, _projectsDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = JsonlSessionParser.ParseSession(path, options);
                    if (session.Turns.Count > 0 || session.Summaries.Count > 0) parsed.Add(session);
                }

                local = SessionSelector.Select(parsed, options, _currentDirectory);
            }
        }

        var chosen = new List<Session>(local);
        chosen.AddRange(shareSessions);

        if (chosen.Count == 0)
        {
            throw new BuildException(
                "Nothing to summarise. No local sessions matched, and no share link was "
                + "imported. Try a wider time window, untick 'this folder only', or paste a "
                + "share link.");
        }

        chosen = SessionOrder.OldestFirst(chosen);

        var text = options.Mode switch
        {
            "transcript" => TranscriptBuilder.Build(chosen, options, now),
            "full" => FullMemoryBuilder.Build(chosen, options, now),
            "json" => JsonExporter.Build(chosen, now),
            _ => SummaryBuilder.Build(chosen, options, now),
        };

        if (options.AsciiOnly) text = AsciiFold.ToAscii(text);

        var prompts = 0;
        var calls = 0;
        foreach (var session in chosen)
        {
            prompts += session.Prompts().Count;
            calls += session.ToolCalls().Count;
        }

        var stats = $"{chosen.Count} session(s) · {prompts} prompt(s) · {calls} tool call(s) "
                    + $"· {RuneCount(text)} chars ~{TokenEstimator.Estimate(text)} tokens";

        return new BuildResult { Text = text, Stats = stats, SessionCount = chosen.Count };
    }

    /// <summary>Imports the share payloads the selected source mode asks for (gui.py:959-982).</summary>
    private async Task<List<Session>> ImportSharesAsync(
        BuildRequest request, MemoryOptions options, CancellationToken cancellationToken)
    {
        var imported = new List<Session>();
        if (request.Source is MemorySource.Local) return imported;

        var shareFile = (request.ShareFile ?? "").Trim();
        var shareUrl = (request.Share ?? "").Trim();
        if (shareUrl.Length == 0 && shareFile.Length == 0)
        {
            throw new BuildException(
                "Paste a claude.ai share link (or a saved payload path) first.");
        }

        if (shareFile.Length > 0)
        {
            var path = PathPolicy.ExpandVariables(shareFile);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new BuildException($"No such file: {path}");
            }

            try
            {
                imported.Add(SharePayloadParser.LoadFile(path, options.IncludeThinking));
            }
            catch (ShareException error)
            {
                throw new BuildException(error.Message, error);
            }
        }

        if (shareUrl.Length > 0)
        {
            JsonElement payload;
            try
            {
                payload = await _shareClient.FetchAsync(shareUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ShareException error)
            {
                throw new BuildException(error.Message, error);
            }

            var session = SharePayloadParser.Parse(payload, includeThinking: options.IncludeThinking);
            imported.Add(session);

            // Cache next to the output so a re-run needs no network (gui.py:976-980). A write
            // failure never discards the payload that is already in memory.
            var cache = System.IO.Path.Combine(
                _defaultOutputDirectory, $"claude-share-{ShortId(session.Sid)}.json");
            ShareCache.TryWrite(cache, payload, cancellationToken);

            if (options.SourceNote.Length == 0)
            {
                options.SourceNote = $"claude.ai share link ({ShareClient.ShareUuid(shareUrl)})";
            }
        }

        return imported;
    }

    /// <summary>Turns the UI payload into the same options the CLI builds (gui.py:904-930).</summary>
    internal static MemoryOptions BuildOptions(
        BuildRequest request, string home, string root, DateTimeOffset now)
    {
        var options = new MemoryOptions
        {
            Home = home,
            Root = root,
            Mode = request.Mode is "memory" or "full" or "transcript" or "json"
                ? request.Mode
                : "memory",
            Format = request.Format is "plain" or "claude-md" ? request.Format : "plain",
            Lang = request.Lang is "auto" or "en" or "ar" or "both" ? request.Lang : "auto",
            Sessions = AsInt(request.Sessions, 0),
            Cwd = request.Cwd ?? "",
            Here = request.Here,
            MaxChars = AsInt(request.MaxChars, 14000),
            MaxItems = AsInt(request.MaxItems, 14),
            IncludeThinking = request.IncludeThinking,
            Sidechains = request.DropSidechains
                ? MemoryOptions.SidechainsDrop
                : MemoryOptions.SidechainsKeep,
            AsciiOnly = request.AsciiOnly,
            SourceNote = request.SourceNote ?? "",
            // The window has its own status line; the renderers must not print anything.
            Quiet = true,
        };

        var since = (request.Since ?? "").Trim();
        if (since.Length > 0)
        {
            try
            {
                options.Since = TimeWindow.ParseSince(since, now);
            }
            catch (FormatException)
            {
                throw new BuildException(
                    $"cannot understand the time window {PythonRepr(since)}. "
                    + "Try 30d, 12h, 30m or 2026-10-01.");
            }
        }

        return options;
    }

    /// <summary>
    /// Number fields accept typed junk from humans; fall back instead of crashing
    /// (gui.py:895). Decimal and Unicode-digit spellings the reference accepts through
    /// <c>int()</c> are not emulated: the UI only ever supplies an <c>&lt;input type=number&gt;</c>
    /// value.
    /// </summary>
    internal static int AsInt(string? value, int fallback)
        => int.TryParse(
            (value ?? "").Trim(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;

    /// <summary>Python <c>sid[:8]</c> for the share cache name (all share ids are ASCII).</summary>
    private static string ShortId(string sid) => sid.Length <= 8 ? sid : sid[..8];

    /// <summary>Python <c>len(text)</c> counts code points, not UTF-16 code units.</summary>
    private static int RuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>Formats a string the way Python's <c>repr()</c> formats the common cases.</summary>
    private static string PythonRepr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var body = value.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote);
        return quote + body + quote;
    }
}
