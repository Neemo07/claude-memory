using System.IO;
using ClaudeMemory.Core.Abstractions;
using Microsoft.Win32;

namespace ClaudeMemory.App.Services;

/// <summary>
/// <see cref="IRegistryStore"/> over <c>HKEY_CURRENT_USER</c>. Nothing here can reach
/// <c>HKEY_LOCAL_MACHINE</c>, which is what AGENTS.md and PRD §8 require (no admin rights).
/// </summary>
public sealed class RegistryStore : IRegistryStore
{
    public bool KeyExists(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    public string? GetValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public IReadOnlyList<string> GetValueNames(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public void SetValue(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                        ?? throw new IOException("cannot create the registry key " + keyPath);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void DeleteKey(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key is null) return;

        // Refuse to remove a key someone else still uses; the caller only wants tidy-up.
        if (key.ValueCount > 0 || key.SubKeyCount > 0) return;

        Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
    }

    public void DeleteKeyTree(string keyPath)
        => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
}
