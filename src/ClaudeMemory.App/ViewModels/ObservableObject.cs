using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeMemory.App.ViewModels;

/// <summary>
/// Minimal change notification base, standing in for an MVVM toolkit
/// (IMPLEMENTATION_PLAN.md:98). View models never reference WPF types, so they are
/// unit-testable without a dispatcher.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
