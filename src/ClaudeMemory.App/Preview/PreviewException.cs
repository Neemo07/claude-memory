namespace ClaudeMemory.App.Preview;

/// <summary>
/// A preview failure whose message is shown to the user as-is. The wording follows
/// gui.py:1027-1049 (<c>read_markdown_file</c>).
/// </summary>
public sealed class PreviewException : Exception
{
    public PreviewException(string message) : base(message)
    {
    }
}
