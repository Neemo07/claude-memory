using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeMemory.Core.Abstractions;

namespace ClaudeMemory.App.Services;

/// <summary>Whether this copy of the app owns the Markdown association (PRD FR-22).</summary>
public enum AssociationState
{
    /// <summary>No per-user registration exists.</summary>
    NotRegistered,

    /// <summary>Registered, and the open command points at this executable.</summary>
    Registered,

    /// <summary>Registered, but the open command points at a different copy.</summary>
    RegisteredForAnotherCopy,
}

/// <summary>
/// Per-user Markdown association (PRD FR-20..FR-24). Registration is only ever an explicit user
/// action, writes only under <c>HKCU\Software\Classes</c> plus the Default Apps capability entry,
/// and never touches <c>UserChoice</c> or the <c>.md</c> default value.
/// </summary>
public interface IAssociationService
{
    AssociationState State { get; }

    /// <summary>The open command currently stored, or an empty string when unregistered.</summary>
    string RegisteredCommand { get; }

    void Register();

    void Unregister();

    /// <summary>Opens Windows' own confirmation surface for a sample <c>.md</c> file.</summary>
    void ShowWindowsConfirmation();
}

public sealed class AssociationService : IAssociationService
{
    /// <summary>ProgID written for the preview handler; stable so uninstall can find it.</summary>
    public const string ProgId = "ClaudeMemory.MarkdownPreview";

    public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    public const string CapabilitiesKey = @"Software\ClaudeMemory\Capabilities";
    public const string CapabilitiesValueName = "claude-memory";
    public const string FriendlyName = "claude-memory";
    public const string ProgIdDescription = "Markdown preview in claude-memory";

    /// <summary>Extensions the app offers to open (PRD FR-20).</summary>
    public static readonly string[] Extensions = { ".md", ".markdown" };

    private readonly IRegistryStore _registry;
    private readonly string _executablePath;
    private readonly Func<string, bool> _fileExists;
    private readonly string _sampleDirectory;
    private readonly Action<string> _showOpenWith;

    public AssociationService(
        IRegistryStore registry,
        string executablePath,
        string? sampleDirectory = null,
        Action<string>? showOpenWith = null,
        Func<string, bool>? fileExists = null)
    {
        _registry = registry;
        _executablePath = executablePath;
        _fileExists = fileExists ?? File.Exists;
        _sampleDirectory = sampleDirectory
                           ?? Path.Combine(Path.GetTempPath(), "claude-memory");
        _showOpenWith = showOpenWith ?? ShellDialogs.ShowOpenWith;
    }

    public static string ProgIdKey => @"Software\Classes\" + ProgId;

    public static string ProgIdCommandKey => ProgIdKey + @"\shell\open\command";

    public string ApplicationsKey
        => @"Software\Classes\Applications\" + Path.GetFileName(_executablePath);

    /// <summary>The open command, quoted so a path with spaces still launches (PRD §10).</summary>
    public string Command => "\"" + _executablePath + "\" \"%1\"";

    public AssociationState State
    {
        get
        {
            var command = _registry.GetValue(ProgIdCommandKey, "");
            if (string.IsNullOrEmpty(command)) return AssociationState.NotRegistered;

            return string.Equals(command, Command, StringComparison.OrdinalIgnoreCase)
                ? AssociationState.Registered
                : AssociationState.RegisteredForAnotherCopy;
        }
    }

    public string RegisteredCommand => _registry.GetValue(ProgIdCommandKey, "") ?? "";

    public void Register()
    {
        // Registering under the .NET SDK host (dotnet run) would point the association at
        // dotnet.exe, a key other tools also use, and unregistering would then delete their
        // subkeys. The same guard catches a stale non-existent path.
        var name = Path.GetFileName(_executablePath);
        if (name is "dotnet.exe" or "dotnet")
        {
            throw new InvalidOperationException(
                "this copy is running under the .NET SDK host (dotnet run); "
                + "publish or run ClaudeMemory.exe directly to register it");
        }

        if (!_fileExists(_executablePath))
        {
            throw new InvalidOperationException(
                "the executable to register does not exist: " + _executablePath);
        }

        _registry.SetValue(ProgIdKey, "", ProgIdDescription);
        _registry.SetValue(ProgIdKey + @"\DefaultIcon", "", "\"" + _executablePath + "\",0");
        _registry.SetValue(ProgIdCommandKey, "", Command);

        // OpenWithProgids adds a candidate without replacing whatever the user already chose.
        foreach (var extension in Extensions)
        {
            _registry.SetValue(
                @"Software\Classes\" + extension + @"\OpenWithProgids", ProgId, "");
        }

        _registry.SetValue(ApplicationsKey, "FriendlyAppName", FriendlyName);
        _registry.SetValue(ApplicationsKey + @"\shell\open\command", "", Command);
        foreach (var extension in Extensions)
        {
            _registry.SetValue(ApplicationsKey + @"\SupportedTypes", extension, "");
        }

        _registry.SetValue(CapabilitiesKey, "ApplicationName", FriendlyName);
        _registry.SetValue(
            CapabilitiesKey,
            "ApplicationDescription",
            "Builds a Markdown memory file from Claude Code sessions.");
        foreach (var extension in Extensions)
        {
            _registry.SetValue(CapabilitiesKey + @"\FileAssociations", extension, ProgId);
        }

        _registry.SetValue(RegisteredApplicationsKey, CapabilitiesValueName, CapabilitiesKey);
    }

    public void Unregister()
    {
        _registry.DeleteValue(RegisteredApplicationsKey, CapabilitiesValueName);
        _registry.DeleteKeyTree(@"Software\ClaudeMemory");

        foreach (var extension in Extensions)
        {
            var openWith = @"Software\Classes\" + extension + @"\OpenWithProgids";
            _registry.DeleteValue(openWith, ProgId);

            // Only remove the container when nothing else is listed; the extension key itself
            // belongs to Explorer and is never deleted.
            _registry.DeleteKey(openWith);
        }

        _registry.DeleteKeyTree(ApplicationsKey);
        _registry.DeleteKeyTree(ProgIdKey);
    }

    public void ShowWindowsConfirmation()
    {
        var sample = WriteSample();
        try
        {
            _showOpenWith(sample);
        }
        finally
        {
            // The sample exists only for the picker; a best-effort delete keeps %TEMP% tidy
            // without ever failing the action.
            try
            {
                File.Delete(sample);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Windows' "Open with" dialog needs a real file, so a throwaway sample is written to the
    /// temp folder. It contains no transcript text.
    /// </summary>
    private string WriteSample()
    {
        Directory.CreateDirectory(_sampleDirectory);
        var path = Path.Combine(_sampleDirectory, "preview-sample.md");
        File.WriteAllText(
            path,
            "# claude-memory preview\n\nThis file was created so Windows can show its app picker.\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

/// <summary>
/// The one place that talks to Windows' own app picker. <c>SHOpenWithDialog</c> is the supported
/// way to let the user confirm a default app; <c>UserChoice</c> is never written (AGENTS.md).
/// </summary>
internal static class ShellDialogs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string pcszFile;
        public string? pcszClass;
        public OpenAsFlags oaifInFlags;
    }

    [Flags]
    private enum OpenAsFlags : uint
    {
        AllowRegistration = 0x00000001,
        RegisterExtension = 0x00000002,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

    public static void ShowOpenWith(string file)
    {
        var info = new OpenAsInfo
        {
            pcszFile = file,
            pcszClass = null,
            oaifInFlags = OpenAsFlags.AllowRegistration | OpenAsFlags.RegisterExtension,
        };

        int result;
        try
        {
            result = SHOpenWithDialog(IntPtr.Zero, ref info);
        }
        catch (DllNotFoundException)
        {
            result = -1;
        }
        catch (EntryPointNotFoundException)
        {
            result = -1;
        }

        // S_OK, or the user pressed Cancel (ERROR_CANCELLED): both are normal answers
        // (PRD §9.30), so only a real failure falls back to the settings page.
        if (result is 0 or Cancelled) return;

        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })
            ?.Dispose();
    }

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c>.</summary>
    private const int Cancelled = unchecked((int)0x800704C7);
}
