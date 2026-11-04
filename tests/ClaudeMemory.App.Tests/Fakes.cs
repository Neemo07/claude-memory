using System.IO;
using ClaudeMemory.App.Services;
using ClaudeMemory.App.ViewModels;

namespace ClaudeMemory.App.Tests;

/// <summary>Build pipeline double: records the request and runs a settable handler.</summary>
internal sealed class FakeBuildPipeline : IBuildPipeline
{
    public Func<BuildRequest, CancellationToken, Task<BuildResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(new BuildResult
        {
            Text = "generated",
            Stats = "1 session(s) · 0 prompt(s) · 0 tool call(s) · 9 chars ~2 tokens",
            SessionCount = 1,
        });

    public BuildRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public Task<BuildResult> BuildAsync(
        BuildRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return Handler(request, cancellationToken);
    }
}

/// <summary>Session scanner double.</summary>
internal sealed class FakeSessionScanner : ISessionScanner
{
    public Func<ScanResult>? Handler { get; set; }

    public ScanResult Result { get; set; } = new()
    {
        Sessions = Array.Empty<SessionRow>(),
        Root = @"C:\home\.claude",
        ProjectsDirectory = @"C:\home\.claude\projects",
        DefaultOutputDirectory = @"C:\home\Documents",
    };

    public int CallCount { get; private set; }

    public ScanResult Scan()
    {
        CallCount++;
        return Handler is null ? Result : Handler();
    }
}

/// <summary>File dialog double; null results mean the user cancelled.</summary>
internal sealed class FakeFileDialogService : IFileDialogService
{
    public string? SaveResult { get; set; }

    public string? OpenResult { get; set; }

    public string? LastSuggestedName { get; private set; }

    public string? LastStartDirectory { get; private set; }

    public string? LastFilter { get; private set; }

    public string? PickSaveFile(string suggestedName, string startDirectory)
    {
        LastSuggestedName = suggestedName;
        LastStartDirectory = startDirectory;
        return SaveResult;
    }

    public string? PickOpenFile(string suggestedName, string startDirectory, string filter)
    {
        LastSuggestedName = suggestedName;
        LastStartDirectory = startDirectory;
        LastFilter = filter;
        return OpenResult;
    }
}

/// <summary>Clipboard double that can simulate a blocked clipboard.</summary>
internal sealed class FakeClipboardService : IClipboardService
{
    public bool Result { get; set; } = true;

    public string? LastText { get; private set; }

    public bool TrySetText(string text)
    {
        LastText = text;
        return Result;
    }
}

/// <summary>Shell double recording the revealed folder.</summary>
internal sealed class FakeShellService : IShellService
{
    public string? LastFolder { get; private set; }

    public string? LastExternal { get; private set; }

    public Exception? OpenExternalError { get; set; }

    public void RevealFolder(string folder) => LastFolder = folder;

    public void OpenExternal(string url)
    {
        LastExternal = url;
        if (OpenExternalError is not null) throw OpenExternalError;
    }
}

/// <summary>Creates and removes a scratch directory for one save test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "claude-memory-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
