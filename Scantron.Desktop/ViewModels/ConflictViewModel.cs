using Scantron.Core.Merge;
using Scantron.Desktop.Mvvm;
using Scantron.Desktop.Services;

namespace Scantron.Desktop.ViewModels;

/// <summary>
/// One unresolved field, with the two competing values side by side.
/// </summary>
/// <remarks>
/// The merger reports a conflict as raw values because it has no opinion about them. This wraps
/// one so the grid can present the decision, and so "resolved" is a state the operator controls
/// rather than something inferred by comparing values after the fact.
/// </remarks>
public sealed class ConflictViewModel : ObservableObject
{
    private bool _isResolved;
    private ConflictResolution? _resolution;

    public ConflictViewModel(ItemConflict source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    public ItemConflict Source { get; }

    public string ContainerId => Source.ContainerId;

    public string ItemName => string.IsNullOrWhiteSpace(Source.ItemName) ? Source.Field : Source.ItemName;

    /// <summary>Field path, e.g. "Quantity". "Presence" means the row vanished remotely.</summary>
    public string Field => Source.Field;

    public string BaseValue => Source.BaseValue ?? "(none)";

    public string LocalValue => Source.LocalValue ?? "(none)";

    public string RemoteValue => Source.RemoteValue ?? "(none)";

    /// <summary>One-line summary for the status bar and the audit trail.</summary>
    public string Summary =>
        $"{(string.IsNullOrWhiteSpace(Source.ItemName) ? ContainerId : Source.ItemName)} - {Field}: " +
        $"desktop {LocalValue}, handheld {RemoteValue}";

    /// <summary>True once the operator has chosen a side.</summary>
    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>The chosen side, or null while the conflict is open.</summary>
    public ConflictResolution? Resolution
    {
        get => _resolution;
        private set
        {
            if (SetProperty(ref _resolution, value))
            {
                IsResolved = value is not null;
            }
        }
    }

    /// <summary>Row highlight for the grid.</summary>
    public string StatusText => Resolution switch
    {
        ConflictResolution.KeepLocal => "Kept desktop",
        ConflictResolution.TakeRemote => "Took handheld",
        _ => "Unresolved",
    };

    /// <summary>Keeps the desktop value, which the merged document already carries.</summary>
    public void KeepLocal() => Resolution = ConflictResolution.KeepLocal;

    /// <summary>Overwrites with the handheld's value.</summary>
    public void TakeRemote() => Resolution = ConflictResolution.TakeRemote;
}
