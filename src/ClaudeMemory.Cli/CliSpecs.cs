using ClaudeMemory.Core.Model;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Cli;

internal enum OptionKind
{
    /// <summary><c>-h/--help</c>: print the help text and exit 0.</summary>
    Help,

    /// <summary><c>--version</c>: print the version and exit 0.</summary>
    Version,

    /// <summary>A store-true/store-false switch.</summary>
    Flag,

    /// <summary>Takes exactly one value.</summary>
    Value,
}

/// <summary>
/// One command-line option, in the order the reference adds it
/// (claude_memory.py:1795-1867). The same table drives parsing and the help text so the two
/// cannot drift.
/// </summary>
internal sealed class OptionSpec
{
    public required string[] Names { get; init; }

    public required OptionKind Kind { get; init; }

    /// <summary>Value placeholder, or empty when the option takes no value.</summary>
    public string Metavar { get; init; } = "";

    /// <summary>Allowed values, rendered as <c>{a,b}</c> in usage and help.</summary>
    public string[]? Choices { get; init; }

    public required string Help { get; init; }

    /// <summary>Replacement for <c>{default}</c> in <see cref="Help"/>.</summary>
    public string? DefaultText { get; init; }

    public Action<MemoryOptions, string>? Assign { get; init; }
}

/// <summary>
/// The reference argument table (claude_memory.py:1795-1867). Order is load-bearing: it is the
/// order of the ambiguous-option candidates and of the help entries.
/// </summary>
internal static class CliSpecs
{
    private static readonly string[] Modes = { "memory", "full", "transcript", "json" };

    private static readonly string[] Formats = { "plain", "claude-md" };

    private static readonly string[] Sidechains = { "keep", "drop" };

    private static readonly string[] Languages = { "auto", "en", "ar", "both" };

    public static List<OptionSpec> Create(DateTimeOffset now) =>
    [
        new()
        {
            Names = ["-h", "--help"],
            Kind = OptionKind.Help,
            Help = "show this help message and exit",
        },
        new()
        {
            Names = ["--version"],
            Kind = OptionKind.Version,
            Help = "show program's version number and exit",
        },
        new()
        {
            Names = ["--root"],
            Kind = OptionKind.Value,
            Metavar = "ROOT",
            Help = "Claude config dir (default: {root})",
            Assign = (options, value) => options.Root = value,
        },
        new()
        {
            Names = ["--projects-dir"],
            Kind = OptionKind.Value,
            Metavar = "PROJECTS_DIR",
            Help = "explicit transcript dir, overrides <root>/projects",
            Assign = (options, value) => options.ProjectsDir = value,
        },
        new()
        {
            Names = ["--out"],
            Kind = OptionKind.Value,
            Metavar = "OUT",
            Help = "write the file here",
            Assign = (options, value) => options.Out = value,
        },
        new()
        {
            Names = ["--print"],
            Kind = OptionKind.Flag,
            Help = "also write to stdout",
            Assign = (options, _) => options.PrintStdout = true,
        },
        new()
        {
            Names = ["--mode"],
            Kind = OptionKind.Value,
            Choices = Modes,
            Help = "memory = summary (default); full = summary plus the entire conversation; "
                   + "transcript = raw readable chat; json = structured",
            Assign = Choice(Modes, (options, value) => options.Mode = value),
        },
        new()
        {
            Names = ["--format"],
            Kind = OptionKind.Value,
            Choices = Formats,
            Help = "claude-md wraps output in CLAUDE.md paste markers",
            Assign = Choice(Formats, (options, value) => options.Format = value),
        },
        new()
        {
            Names = ["--sessions"],
            Kind = OptionKind.Value,
            Metavar = "SESSIONS",
            Help = "only the newest N sessions (0=all)",
            Assign = Integer((options, value) => options.Sessions = value),
        },
        new()
        {
            Names = ["--since"],
            Kind = OptionKind.Value,
            Metavar = "WHEN",
            Help = "7d, 12h, 30m, YYYY-MM-DD or ISO timestamp",
            Assign = (options, value) =>
            {
                try
                {
                    options.Since = TimeWindow.ParseSince(value, now);
                }
                catch (FormatException error)
                {
                    throw new CliValueException(error.Message);
                }
            },
        },
        new()
        {
            Names = ["--fast"],
            Kind = OptionKind.Flag,
            Help = "APPROXIMATE: skip transcripts whose file mtime predates --before deciding, "
                   + "instead of reading every one. Much faster on big histories, but can miss "
                   + "sessions whose timestamps live inside the record and whose file was "
                   + "moved/restored. Needs --since to do anything",
            Assign = (options, _) => options.Fast = true,
        },
        new()
        {
            Names = ["--cwd"],
            Kind = OptionKind.Value,
            Metavar = "CWD",
            Help = "only sessions whose path/cwd contains this",
            Assign = (options, value) => options.Cwd = value,
        },
        new()
        {
            Names = ["--here"],
            Kind = OptionKind.Flag,
            Help = "only sessions belonging to the current directory (default cwd filter)",
            Assign = (options, _) => options.Here = true,
        },
        new()
        {
            Names = ["--branch"],
            Kind = OptionKind.Value,
            Metavar = "BRANCH",
            Help = "only sessions on this git branch",
            Assign = (options, value) => options.Branch = value,
        },
        new()
        {
            Names = ["--max-chars"],
            Kind = OptionKind.Value,
            Metavar = "MAX_CHARS",
            Help = "global body budget in chars (default: {default})",
            DefaultText = "14000",
            Assign = Integer((options, value) => options.MaxChars = value),
        },
        new()
        {
            Names = ["--max-items"],
            Kind = OptionKind.Value,
            Metavar = "MAX_ITEMS",
            Help = "max lines per claim section",
            Assign = Integer((options, value) => options.MaxItems = value),
        },
        new()
        {
            Names = ["--max-paths"],
            Kind = OptionKind.Value,
            Metavar = "MAX_PATHS",
            Help = "max paths/identifiers listed",
            Assign = Integer((options, value) => options.MaxPaths = value),
        },
        new()
        {
            Names = ["--max-cmds"],
            Kind = OptionKind.Value,
            Metavar = "MAX_CMDS",
            Help = "max shell commands listed",
            Assign = Integer((options, value) => options.MaxCmds = value),
        },
        new()
        {
            Names = ["--max-line"],
            Kind = OptionKind.Value,
            Metavar = "MAX_LINE",
            Help = "max chars per quoted line",
            Assign = Integer((options, value) => options.MaxLine = value),
        },
        new()
        {
            Names = ["--max-prompts"],
            Kind = OptionKind.Value,
            Metavar = "MAX_PROMPTS_PER_SESSION",
            Help = "max user prompts quoted per session",
            Assign = Integer((options, value) => options.MaxPromptsPerSession = value),
        },
        new()
        {
            Names = ["--max-file-bytes"],
            Kind = OptionKind.Value,
            Metavar = "MAX_FILE_BYTES",
            Help = "per-transcript read cap; tail is kept (default: {default})",
            DefaultText = (12 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assign = Integer((options, value) => options.MaxFileBytes = value),
        },
        new()
        {
            Names = ["--sidechains"],
            Kind = OptionKind.Value,
            Choices = Sidechains,
            Help = "include subagent turns (default: keep)",
            Assign = Choice(Sidechains, (options, value) => options.Sidechains = value),
        },
        new()
        {
            Names = ["--include-thinking"],
            Kind = OptionKind.Flag,
            Help = "include thinking blocks",
            Assign = (options, _) => options.IncludeThinking = true,
        },
        new()
        {
            Names = ["--no-addresses"],
            Kind = OptionKind.Flag,
            Help = "skip bare hex/address harvesting",
            Assign = (options, _) => options.IncludeAddresses = false,
        },
        new()
        {
            Names = ["--history"],
            Kind = OptionKind.Value,
            Metavar = "HISTORY",
            Help = "keep N previous versions in <out>.history (default: {default})",
            DefaultText = "5",
            Assign = Integer((options, value) => options.History = value),
        },
        new()
        {
            Names = ["--source-note"],
            Kind = OptionKind.Value,
            Metavar = "SOURCE_NOTE",
            Help = "free-text provenance line for the header",
            Assign = (options, value) => options.SourceNote = value,
        },
        new()
        {
            Names = ["--share"],
            Kind = OptionKind.Value,
            Metavar = "URL",
            Help = "also import a claude.ai share link "
                   + "(https://claude.ai/share/<uuid>, or a bare uuid)",
            Assign = (options, value) => options.Share = value,
        },
        new()
        {
            Names = ["--share-file"],
            Kind = OptionKind.Value,
            Metavar = "PATH",
            Help = "import a saved share payload (.json) instead of fetching",
            Assign = (options, value) => options.ShareFile = value,
        },
        new()
        {
            Names = ["--share-save"],
            Kind = OptionKind.Value,
            Metavar = "PATH",
            Help = "cache the fetched share payload here for offline re-runs",
            Assign = (options, value) => options.ShareSave = value,
        },
        new()
        {
            Names = ["--only-share"],
            Kind = OptionKind.Flag,
            Help = "ignore local sessions, use only the shared link",
            Assign = (options, _) => options.OnlyShare = true,
        },
        new()
        {
            Names = ["--ascii"],
            Kind = OptionKind.Flag,
            Help = "downgrade box/arrow glyphs to ASCII (legacy consoles)",
            Assign = (options, _) => options.AsciiOnly = true,
        },
        new()
        {
            Names = ["--lang"],
            Kind = OptionKind.Value,
            Choices = Languages,
            Help = "section header language: auto-detect (default), English, Arabic, or bilingual",
            Assign = Choice(Languages, (options, value) => options.Lang = value),
        },
        new()
        {
            Names = ["--list"],
            Kind = OptionKind.Flag,
            Help = "list discovered sessions and exit",
            Assign = (options, _) => options.ListOnly = true,
        },
        new()
        {
            Names = ["--quiet"],
            Kind = OptionKind.Flag,
            Help = "suppress the stderr summary",
            Assign = (options, _) => options.Quiet = true,
        },
    ];

    /// <summary>argparse <c>_check_value</c>: <c>invalid choice: 'x' (choose from a, b)</c>.</summary>
    private static Action<MemoryOptions, string> Choice(
        string[] choices, Action<MemoryOptions, string> assign) => (options, value) =>
    {
        if (!choices.Contains(value, StringComparer.Ordinal))
        {
            throw new CliValueException(
                $"invalid choice: {PyText.Repr(value)} (choose from {string.Join(", ", choices)})");
        }

        assign(options, value);
    };

    private static Action<MemoryOptions, string> Integer(Action<MemoryOptions, int> assign)
        => (options, value) => assign(options, PyInt.Parse(value));
}