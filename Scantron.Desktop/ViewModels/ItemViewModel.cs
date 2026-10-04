using System.Globalization;
using Scantron.Core.Models;
using Scantron.Desktop.Mvvm;

namespace Scantron.Desktop.ViewModels;

/// <summary>
/// Editable view of a single item row.
/// </summary>
/// <remarks>
/// <para>
/// Holds an immutable <see cref="Item"/> snapshot rather than being one. Every edit produces a
/// new domain record via <c>with</c>, which means <see cref="Domain"/> is always a consistent,
/// fully-formed <see cref="Item"/> - never a half-typed grid row. That is what lets the export
/// path treat the view model as a plain document source.
/// </para>
/// <para>
/// <see cref="Touch"/> stamps <c>updatedAt</c> on every edit. This is not cosmetic: the merger
/// decides who changed a row by comparing timestamps against the base, so a field edited in the
/// desktop and never re-stamped would be indistinguishable from an untouched row and the edit
/// would be reverted by the next sync.
/// </para>
/// </remarks>
public sealed class ItemViewModel : ObservableObject
{
    private string _name = "";
    private string _barcode = "";
    private int _quantity = 1;
    private string _category = "";
    private string _notes = "";

    public ItemViewModel(Item source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Identity is minted on the way in rather than on save, so a row that exists in the
        // UI already has the uuid the merge keys on. Without it, two rows with the same
        // name-only identity would merge into one at sync time.
        Uuid = source.Uuid;
        Name = source.Name;
        Barcode = source.Barcode;
        Quantity = source.Quantity;
        Category = source.Category;
        Notes = source.Notes;
        UpdatedAt = source.UpdatedAt;
    }

    /// <summary>Stable identity. Empty only for a brand-new row, before first edit.</summary>
    public string Uuid { get; private set; }

    /// <summary>Epoch milliseconds of the last edit.</summary>
    public long UpdatedAt { get; private set; }

    /// <summary>True when this row carries no barcode, matching <see cref="Item.IsNameOnly"/>.</summary>
    public bool IsNameOnly => string.IsNullOrWhiteSpace(_barcode);

    /// <summary>
    /// True when the row has no identity yet, which the operator should be told about because
    /// name-only matching is ambiguous when two rows share a name in one container.
    /// </summary>
    public bool NeedsIdentity => string.IsNullOrWhiteSpace(Uuid);

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? ""))
            {
                Touch();
            }
        }
    }

    public string Barcode
    {
        get => _barcode;
        set
        {
            if (SetProperty(ref _barcode, value ?? ""))
            {
                // IsNameOnly drives the barcode editor's affordance, so it has to be re-notified
                // alongside the value itself.
                OnPropertyChanged(nameof(IsNameOnly));
                OnPropertyChanged(nameof(IdentityHint));
                Touch();
            }
        }
    }

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, value))
            {
                Touch();
            }
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value ?? ""))
            {
                Touch();
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value ?? ""))
            {
                Touch();
            }
        }
    }

    /// <summary>Explains how this row will be matched during a merge.</summary>
    public string IdentityHint => !string.IsNullOrWhiteSpace(Uuid)
        ? "Matched by uuid"
        : IsNameOnly
            ? "Matched by name only - rename two rows the same and they will merge"
            : "Matched by barcode";

    /// <summary>
    /// Advances <see cref="UpdatedAt"/> past every other row in the document.
    /// </summary>
    /// <remarks>
    /// The merger flags any timestamp more than <see cref="Core.Merge.InventoryMerger.MaxPlausibleSkew"/>
    /// from the local clock as evidence of a wrong device clock. Editing a row stamps it with
    /// "now" precisely so a desktop edit never trips that check.
    /// </remarks>
    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        OnPropertyChanged(nameof(UpdatedAt));
    }

    /// <summary>Mints a uuid for a row that does not have one.</summary>
    public void EnsureIdentity()
    {
        if (!string.IsNullOrWhiteSpace(Uuid))
        {
            return;
        }

        Uuid = Guid.NewGuid().ToString();
        OnPropertyChanged(nameof(Uuid));
        OnPropertyChanged(nameof(NeedsIdentity));
        OnPropertyChanged(nameof(IdentityHint));
        Touch();
    }

    /// <summary>Projects this view back to an immutable <see cref="Item"/>.</summary>
    public Item ToItem() => new()
    {
        // A blank name is still written through: the validator, not the grid, decides that an
        // unnamed row is a hard error, so the operator sees the real reason on export.
        Uuid = Uuid,
        Name = Name.Trim(),
        Barcode = Barcode.Trim(),
        Quantity = Quantity,
        Category = Category.Trim(),
        Notes = Notes.Trim(),
        UpdatedAt = UpdatedAt,
    };

    /// <summary>Replaces every field from <paramref name="source"/>, discarding local edits.</summary>
    public void Load(Item source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Uuid = source.Uuid;
        Name = source.Name;
        Barcode = source.Barcode;
        Quantity = source.Quantity;
        Category = source.Category;
        Notes = source.Notes;
        UpdatedAt = source.UpdatedAt;

        OnPropertyChanged(nameof(Uuid));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(IsNameOnly));
        OnPropertyChanged(nameof(NeedsIdentity));
        OnPropertyChanged(nameof(IdentityHint));
    }

    public override string ToString() =>
        $"{Name} x{Quantity.ToString(CultureInfo.InvariantCulture)}";
}
