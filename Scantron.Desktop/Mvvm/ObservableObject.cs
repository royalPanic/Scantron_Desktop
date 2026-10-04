using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Scantron.Desktop.Mvvm;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for view models.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from an MVVM package: this app needs property-change
/// notification and nothing more, and a package would add a restore-time dependency to a build
/// that has to stay reproducible on an offline workstation.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns <paramref name="field"/> and raises change notification if it moved.</summary>
    /// <returns><see langword="true"/> when the value actually changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
