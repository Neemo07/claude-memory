namespace ClaudeMemory.App.Services;

/// <summary>Native Open/Save dialogs, abstracted so view-model tests never open a window.</summary>
public interface IFileDialogService
{
    /// <summary>Shows a Save As dialog; returns null when the user cancels.</summary>
    string? PickSaveFile(string suggestedName, string startDirectory);

    /// <summary>Shows an Open dialog; <paramref name="filter"/> uses the Win32
    /// <c>description|pattern|...</c> form. Returns null when the user cancels.</summary>
    string? PickOpenFile(string suggestedName, string startDirectory, string filter);
}

/// <summary>Clipboard access that reports failure instead of throwing (gui.py:809-825).</summary>
public interface IClipboardService
{
    bool TrySetText(string text);
}

/// <summary>Shell actions outside the window, such as revealing a folder (gui.py:1106-1116).</summary>
public interface IShellService
{
    void RevealFolder(string folder);
}

/// <summary>Save-dialog filter for a saved share payload (gui.py:1154).</summary>
public static class DialogFilters
{
    public const string SharePayload =
        "Saved share payload (*.json;*.md;*.markdown;*.txt)|*.json;*.md;*.markdown;*.txt"
        + "|All files (*.*)|*.*";
}
