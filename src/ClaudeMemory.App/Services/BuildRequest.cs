namespace ClaudeMemory.App.Services;

/// <summary>The three source choices the UI offers (gui.py:281-286, <c>name="src"</c>).</summary>
public enum MemorySource
{
    Local,
    Both,
    ShareOnly,
}

/// <summary>
/// One Generate request: the UI shape of the <c>/api/build</c> payload
/// (gui.py:735-752). Numeric fields stay strings because the reference accepts typed junk
/// and falls back instead of failing (gui.py:895, <c>as_int</c>).
/// </summary>
public sealed class BuildRequest
{
    public MemorySource Source { get; init; } = MemorySource.Local;

    public string Share { get; init; } = "";

    public string ShareFile { get; init; } = "";

    public bool Here { get; init; }

    public string Since { get; init; } = "";

    public string Sessions { get; init; } = "0";

    public string Cwd { get; init; } = "";

    public string Mode { get; init; } = "memory";

    public string Format { get; init; } = "plain";

    public string Lang { get; init; } = "auto";

    public string MaxChars { get; init; } = "14000";

    public string MaxItems { get; init; } = "14";

    public bool IncludeThinking { get; init; }

    public bool DropSidechains { get; init; }

    public bool AsciiOnly { get; init; }

    public string SourceNote { get; init; } = "";
}

/// <summary>The generated document plus the reference status line (gui.py:1012-1014).</summary>
public sealed class BuildResult
{
    public required string Text { get; init; }

    public required string Stats { get; init; }

    public int SessionCount { get; init; }
}

/// <summary>A user-facing build failure; the message is shown in the status line as-is.</summary>
public sealed class BuildException : Exception
{
    public BuildException(string message) : base(message)
    {
    }

    public BuildException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The generation pipeline the shell drives, split out so tests fake it.</summary>
public interface IBuildPipeline
{
    Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default);
}
