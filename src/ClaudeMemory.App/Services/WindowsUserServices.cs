using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ClaudeMemory.App.Services;

/// <summary>WPF implementations of the shell services. All are thin, UI-thread only.</summary>
public sealed class FileDialogService : IFileDialogService
{
    public string? PickSaveFile(string suggestedName, string startDirectory)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            InitialDirectory = Directory.Exists(startDirectory) ? startDirectory : "",
            Filter = "Markdown (*.md)|*.md|Markdown (*.markdown)|*.markdown"
                     + "|JSON (*.json)|*.json|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string suggestedName, string startDirectory, string filter)
    {
        var dialog = new OpenFileDialog
        {
            FileName = suggestedName,
            InitialDirectory = Directory.Exists(startDirectory) ? startDirectory : "",
            Filter = filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static Window? Owner => Application.Current?.MainWindow;
}

/// <summary>Clipboard writes report failure rather than throwing (PRD §9.18).</summary>
public sealed class ClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
                                          or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Reveals a folder in Explorer (gui.py:1112-1115).</summary>
public sealed class ShellService : IShellService
{
    public void RevealFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
