namespace ClaudeMemory.Core.Abstractions;

/// <summary>
/// The minimal per-user registry surface the file association needs (PRD FR-20..FR-22).
/// </summary>
/// <remarks>
/// Paths are relative to <c>HKEY_CURRENT_USER</c>, so <c>Software\Classes\...</c> is the most a
/// caller ever names. The empty value name means the key's default value. <see cref="DeleteKey"/>
/// removes a key only when it is already empty; <see cref="DeleteKeyTree"/> is for subtrees the
/// app itself created, never for a key Explorer also writes to.
/// </remarks>
public interface IRegistryStore
{
    bool KeyExists(string keyPath);

    string? GetValue(string keyPath, string valueName);

    IReadOnlyList<string> GetValueNames(string keyPath);

    IReadOnlyList<string> GetSubKeyNames(string keyPath);

    void SetValue(string keyPath, string valueName, string value);

    void DeleteValue(string keyPath, string valueName);

    /// <summary>Deletes an empty key. A key with values or subkeys is left alone.</summary>
    void DeleteKey(string keyPath);

    /// <summary>Deletes a key and everything under it. Only for app-owned subtrees.</summary>
    void DeleteKeyTree(string keyPath);
}
