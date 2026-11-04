using ClaudeMemory.App.Services;
using ClaudeMemory.Core.Abstractions;

namespace ClaudeMemory.App.Tests;

/// <summary>
/// Registry double that records every write, so a test can prove the app touches nothing it
/// does not own (PRD §8: confined to <c>HKCU\Software\Classes</c>, no <c>UserChoice</c>).
/// </summary>
internal sealed class InMemoryRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> TouchedPaths { get; } = new();

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public string? GetValue(string keyPath, string valueName)
        => _keys.TryGetValue(keyPath, out var values)
           && values.TryGetValue(valueName, out var value)
            ? value
            : null;

    public IReadOnlyList<string> GetValueNames(string keyPath)
        => _keys.TryGetValue(keyPath, out var values)
            ? values.Keys.ToArray()
            : Array.Empty<string>();

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        var prefix = keyPath + @"\";
        return _keys.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..])
            .Where(rest => !rest.Contains('\\'))
            .ToArray();
    }

    public void SetValue(string keyPath, string valueName, string value)
    {
        TouchedPaths.Add(keyPath);

        // Like Registry.CreateSubKey: every missing ancestor is created too.
        var path = keyPath;
        while (!string.IsNullOrEmpty(path))
        {
            if (!_keys.ContainsKey(path))
            {
                _keys[path] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var separator = path.LastIndexOf('\\');
            if (separator <= 0) break;
            path = path[..separator];
        }

        _keys[keyPath][valueName] = value;
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        TouchedPaths.Add(keyPath);
        if (_keys.TryGetValue(keyPath, out var values)) values.Remove(valueName);
    }

    public void DeleteKey(string keyPath)
    {
        TouchedPaths.Add(keyPath);
        if (!_keys.TryGetValue(keyPath, out var values)) return;
        if (values.Count > 0) return;
        if (GetSubKeyNames(keyPath).Count > 0) return;

        _keys.Remove(keyPath);
    }

    public void DeleteKeyTree(string keyPath)
    {
        TouchedPaths.Add(keyPath);
        var prefix = keyPath + @"\";
        foreach (var key in _keys.Keys.ToList())
        {
            if (key.Equals(keyPath, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _keys.Remove(key);
            }
        }
    }
}

/// <summary>Association double: records the calls and lets a test simulate a policy refusal.</summary>
internal sealed class FakeAssociationService : IAssociationService
{
    public AssociationState State { get; set; } = AssociationState.NotRegistered;

    public string RegisteredCommand => State switch
    {
        AssociationState.Registered => "\"C:\\app\\ClaudeMemory.exe\" \"%1\"",
        AssociationState.RegisteredForAnotherCopy => "\"C:\\old\\ClaudeMemory.exe\" \"%1\"",
        _ => "",
    };

    public int RegisterCalls { get; private set; }

    public int UnregisterCalls { get; private set; }

    public int ConfirmationCalls { get; private set; }

    public Exception? RegisterError { get; set; }

    public Exception? UnregisterError { get; set; }

    public Exception? ConfirmationError { get; set; }

    public void Register()
    {
        RegisterCalls++;
        if (RegisterError is not null) throw RegisterError;
        State = AssociationState.Registered;
    }

    public void Unregister()
    {
        UnregisterCalls++;
        if (UnregisterError is not null) throw UnregisterError;
        State = AssociationState.NotRegistered;
    }

    public void ShowWindowsConfirmation()
    {
        ConfirmationCalls++;
        if (ConfirmationError is not null) throw ConfirmationError;
    }
}
