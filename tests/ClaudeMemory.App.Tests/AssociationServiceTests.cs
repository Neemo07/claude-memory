using System.IO;
using ClaudeMemory.App.Services;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// The exact registry contract from PRD FR-20..FR-22 and AGENTS.md: per-user only, quoted
/// command, reversible, and nothing that belongs to another handler.
/// </summary>
public sealed class AssociationServiceTests
{
    private const string Exe = @"C:\app\ClaudeMemory.exe";

    private static AssociationService NewService(
        InMemoryRegistryStore registry,
        string executable = Exe,
        string? sampleDirectory = null,
        Action<string>? showOpenWith = null,
        Func<string, bool>? fileExists = null)
        => new(registry, executable, sampleDirectory, showOpenWith, fileExists ?? (_ => true));

    [Fact]
    public void Register_writes_the_progid_icon_and_quoted_command()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);

        service.Register();

        Assert.Equal(
            AssociationService.ProgIdDescription,
            registry.GetValue(AssociationService.ProgIdKey, ""));
        Assert.Equal(
            "\"" + Exe + "\",0", registry.GetValue(AssociationService.ProgIdKey + @"\DefaultIcon", ""));
        Assert.Equal(
            "\"" + Exe + "\" \"%1\"",
            registry.GetValue(AssociationService.ProgIdCommandKey, ""));
    }

    [Fact]
    public void Register_adds_every_extension_to_open_with_progids()
    {
        var registry = new InMemoryRegistryStore();
        NewService(registry).Register();

        foreach (var extension in AssociationService.Extensions)
        {
            Assert.Equal(
                "",
                registry.GetValue(@"Software\Classes\" + extension + @"\OpenWithProgids",
                    AssociationService.ProgId));
        }
    }

    [Fact]
    public void Register_writes_the_application_entry_and_default_apps_capability()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);

        service.Register();

        Assert.Equal(
            "claude-memory", registry.GetValue(service.ApplicationsKey, "FriendlyAppName"));
        Assert.Equal(
            "\"" + Exe + "\" \"%1\"",
            registry.GetValue(service.ApplicationsKey + @"\shell\open\command", ""));
        Assert.Equal(
            "", registry.GetValue(service.ApplicationsKey + @"\SupportedTypes", ".md"));
        Assert.Equal(
            "", registry.GetValue(service.ApplicationsKey + @"\SupportedTypes", ".markdown"));

        Assert.Equal(
            "claude-memory", registry.GetValue(AssociationService.CapabilitiesKey, "ApplicationName"));
        Assert.Equal(
            AssociationService.ProgId,
            registry.GetValue(AssociationService.CapabilitiesKey + @"\FileAssociations", ".md"));
        Assert.Equal(
            AssociationService.CapabilitiesKey,
            registry.GetValue(
                AssociationService.RegisteredApplicationsKey,
                AssociationService.CapabilitiesValueName));
    }

    [Fact]
    public void The_command_quotes_a_path_that_contains_spaces()
    {
        const string spaced = @"C:\Program Files\claude-memory\ClaudeMemory.exe";
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry, spaced);

        service.Register();

        Assert.Equal("\"" + spaced + "\" \"%1\"", service.Command);
        Assert.Equal(
            "\"" + spaced + "\" \"%1\"", registry.GetValue(AssociationService.ProgIdCommandKey, ""));
    }

    [Fact]
    public void The_application_key_uses_the_real_executable_name()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry, @"C:\tools\renamed.exe");

        service.Register();

        Assert.EndsWith(@"Applications\renamed.exe", service.ApplicationsKey, StringComparison.Ordinal);
        Assert.True(registry.KeyExists(@"Software\Classes\Applications\renamed.exe"));
    }

    [Fact]
    public void State_is_not_registered_before_the_first_register()
    {
        var service = NewService(new InMemoryRegistryStore());

        Assert.Equal(AssociationState.NotRegistered, service.State);
        Assert.Equal("", service.RegisteredCommand);
    }

    [Fact]
    public void State_is_registered_for_this_copy_after_register()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);

        service.Register();

        Assert.Equal(AssociationState.Registered, service.State);
        Assert.Equal(service.Command, service.RegisteredCommand);
    }

    [Fact]
    public void State_reports_another_copy_and_the_stored_command()
    {
        var registry = new InMemoryRegistryStore();
        registry.SetValue(
            AssociationService.ProgIdCommandKey, "", "\"C:\\old\\ClaudeMemory.exe\" \"%1\"");

        var service = NewService(registry);

        Assert.Equal(AssociationState.RegisteredForAnotherCopy, service.State);
        Assert.Equal("\"C:\\old\\ClaudeMemory.exe\" \"%1\"", service.RegisteredCommand);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);

        service.Register();
        service.Register();

        Assert.Equal(AssociationState.Registered, service.State);
        Assert.Single(registry.GetValueNames(AssociationService.ProgIdKey + @"\shell\open\command"));
    }

    [Fact]
    public void Unregister_removes_every_key_the_app_created()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);
        service.Register();

        service.Unregister();

        Assert.Equal(AssociationState.NotRegistered, service.State);
        Assert.False(registry.KeyExists(AssociationService.ProgIdKey));
        Assert.False(registry.KeyExists(service.ApplicationsKey));
        Assert.False(registry.KeyExists(@"Software\ClaudeMemory"));
        Assert.Null(registry.GetValue(
            AssociationService.RegisteredApplicationsKey,
            AssociationService.CapabilitiesValueName));
        Assert.Empty(registry.GetValueNames(
            @"Software\Classes\.md\OpenWithProgids"));
    }

    [Fact]
    public void Unregister_leaves_another_handler_alone()
    {
        var registry = new InMemoryRegistryStore();
        registry.SetValue(@"Software\Classes\.md", "", "Other.Markdown");
        registry.SetValue(@"Software\Classes\.md\OpenWithProgids", "Other.Markdown", "");
        registry.SetValue(@"Software\Classes\.md\shell\open\command", "", "\"C:\\other.exe\" \"%1\"");
        registry.SetValue(@"Software\Classes\Applications\other.exe\shell\open\command", "",
            "\"C:\\other.exe\" \"%1\"");

        var service = NewService(registry);
        service.Register();
        service.Unregister();

        Assert.Equal("Other.Markdown", registry.GetValue(@"Software\Classes\.md", ""));
        Assert.Equal(
            "", registry.GetValue(@"Software\Classes\.md\OpenWithProgids", "Other.Markdown"));
        Assert.Equal(
            "\"C:\\other.exe\" \"%1\"",
            registry.GetValue(@"Software\Classes\.md\shell\open\command", ""));
        Assert.True(registry.KeyExists(@"Software\Classes\Applications\other.exe"));

        // Our own ProgID is gone, but the shared OpenWithProgids key survives with the other app.
        Assert.False(registry.KeyExists(AssociationService.ProgIdKey));
    }

    [Fact]
    public void Unregister_keeps_an_empty_extension_key()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);
        service.Register();

        service.Unregister();

        Assert.True(registry.KeyExists(@"Software\Classes\.md"));
        Assert.True(registry.KeyExists(@"Software\Classes\.markdown"));
    }

    [Fact]
    public void Nothing_is_written_outside_the_per_user_classes_tree_and_capability()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(registry);

        service.Register();
        service.Unregister();

        foreach (var path in registry.TouchedPaths)
        {
            Assert.DoesNotContain("UserChoice", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HKLM", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HKEY_LOCAL_MACHINE", path, StringComparison.OrdinalIgnoreCase);

            var allowed = path.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith(@"Software\ClaudeMemory", StringComparison.OrdinalIgnoreCase)
                          || path.Equals(
                              AssociationService.RegisteredApplicationsKey,
                              StringComparison.OrdinalIgnoreCase);
            Assert.True(allowed, "unexpected registry path: " + path);
        }
    }

    [Fact]
    public void UserChoice_is_never_read_or_written()
    {
        var registry = new InMemoryRegistryStore();
        registry.SetValue(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice",
            "ProgId",
            "Apps1");
        registry.TouchedPaths.Clear();

        var service = NewService(registry);
        service.Register();
        service.Unregister();

        // The store recorded no write for that path, and the value is unchanged.
        Assert.DoesNotContain(
            registry.TouchedPaths,
            path => path.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "Apps1",
            registry.GetValue(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice",
                "ProgId"));
    }

    [Fact]
    public void Register_refuses_the_sdk_host_executable()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(
            registry, @"C:\Program Files\dotnet\dotnet.exe", fileExists: _ => true);

        var error = Assert.Throws<InvalidOperationException>(service.Register);

        Assert.Contains("dotnet run", error.Message, StringComparison.Ordinal);
        Assert.Empty(registry.TouchedPaths);
    }

    [Fact]
    public void Register_refuses_a_missing_executable()
    {
        var registry = new InMemoryRegistryStore();
        var service = NewService(
            registry, @"C:\app\ClaudeMemory.exe", fileExists: _ => false);

        var error = Assert.Throws<InvalidOperationException>(service.Register);

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Empty(registry.TouchedPaths);
    }

    [Fact]
    public void The_confirmation_sample_is_a_real_markdown_file_without_transcript_text()
    {
        using var temp = new TempDirectory();
        string? shown = null;
        var existedDuringDialog = false;
        var contentDuringDialog = "";
        var service = NewService(
            new InMemoryRegistryStore(), Exe, temp.Path, path =>
            {
                shown = path;

                // The sample is deleted once the picker returns, so capture inside the dialog.
                existedDuringDialog = File.Exists(path);
                if (existedDuringDialog) contentDuringDialog = File.ReadAllText(path);
            });

        service.ShowWindowsConfirmation();

        Assert.NotNull(shown);
        Assert.EndsWith(".md", shown, StringComparison.OrdinalIgnoreCase);
        Assert.True(existedDuringDialog);
        Assert.Contains("# claude-memory preview", contentDuringDialog, StringComparison.Ordinal);
        Assert.Equal(temp.Path, Path.GetDirectoryName(shown));
        Assert.False(File.Exists(shown), "the sample should be cleaned up afterwards");
    }
}
