namespace ClaudeMemory.Cli.Tests;

/// <summary>
/// A temporary directory removed on dispose, so IO tests never touch the repository or the
/// user's folders.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "claude-memory-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

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
