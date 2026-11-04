namespace ClaudeMemory.Core.Model;

/// <summary>Command/UI options (claude_memory.py:1760).</summary>
public sealed class MemoryOptions
{
    public const string SidechainsKeep = "keep";
    public const string SidechainsDrop = "drop";

    public string Root { get; set; } = DefaultRoot();

    public string? ProjectsDir { get; set; }

    public string? Out { get; set; }

    public bool PrintStdout { get; set; }

    public string Mode { get; set; } = "memory";

    public string Format { get; set; } = "plain";

    public int Sessions { get; set; }

    public DateTimeOffset? Since { get; set; }

    public bool Fast { get; set; }

    public string Cwd { get; set; } = "";

    public bool Here { get; set; }

    public string Branch { get; set; } = "";

    public int MaxChars { get; set; } = 14000;

    public int MaxItems { get; set; } = 14;

    public int MaxPaths { get; set; } = 18;

    public int MaxCmds { get; set; } = 10;

    public int MaxLine { get; set; } = 260;

    public int MaxPromptsPerSession { get; set; } = 12;

    public int MaxFileBytes { get; set; } = 12 * 1024 * 1024;

    public string Sidechains { get; set; } = SidechainsKeep;

    public bool IncludeThinking { get; set; }

    public bool IncludeAddresses { get; set; } = true;

    public int History { get; set; } = 5;

    public bool Quiet { get; set; }

    public bool ListOnly { get; set; }

    public string SourceNote { get; set; } = "";

    public bool AsciiOnly { get; set; }

    public string Lang { get; set; } = "auto";

    public string Share { get; set; } = "";

    public string? ShareFile { get; set; }

    public string? ShareSave { get; set; }

    public bool OnlyShare { get; set; }

    /// <summary>
    /// Home directory used by <c>fmt_cwd</c> to shorten paths to <c>~/...</c>
    /// (claude_memory.py:39).
    /// </summary>
    public string Home { get; set; } = DefaultHome();

    /// <summary>Mirrors <c>HOME</c> (claude_memory.py:39).</summary>
    public static string DefaultHome()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return home;
    }

    /// <summary>Mirrors <c>DEFAULT_ROOT</c> (claude_memory.py:39-40): <c>&lt;home&gt;/.claude</c>.</summary>
    public static string DefaultRoot() => System.IO.Path.Combine(DefaultHome(), ".claude");
}
