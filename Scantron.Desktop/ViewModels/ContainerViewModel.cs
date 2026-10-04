using System.Collections.ObjectModel;
using Scantron.Core.Models;
using Scantron.Desktop.Mvvm;

namespace Scantron.Desktop.ViewModels;

/// <summary>
/// Editable view of one container and the rows inside it.
/// </summary>
/// <remarks>
/// Owns an <see cref="ObservableCollection{T}"/> of items because the grid adds and removes rows
/// in place, and that mutation has to reach the UI without a full rebuild. Container metadata
/// fields touch the timestamp for the same reason item fields do: the merger reads
/// <c>updatedAt</c> to decide whether a side actually changed something.
/// </remarks>
public sealed class ContainerViewModel : ObservableObject
{
    private string _id = "";
    private string _name = "";
    private string _location = "";
    private string _notes = "";

    public ContainerViewModel(Container? source = null)
    {
        _id = source?.Id ?? "";
        _name = source?.Name ?? "";
        _location = source?.Location ?? "";
        _notes = source?.Notes ?? "";
        UpdatedAt = source?.UpdatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (source is not null)
        {
            foreach (var item in source.Items)
            {
                Items.Add(new ItemViewModel(item));
            }
        }

        AddItemCommand = new RelayCommand(AddItem);
        RemoveItemCommand = new RelayCommand(RemoveItem, CanRemoveItem);
    }

    public long UpdatedAt { get; private set; }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public RelayCommand AddItemCommand { get; }

    public RelayCommand RemoveItemCommand { get; }

    /// <summary>Total units across every row, shown in the container list.</summary>
    public int TotalQuantity
    {
        get
        {
            var total = 0;
            foreach (var item in Items)
            {
                total += item.Quantity;
            }

            return total;
        }
    }

    /// <summary>Rows still missing a uuid, which merge on name alone until one is minted.</summary>
    public int ItemsNeedingIdentity
    {
        get
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item.NeedsIdentity)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Container tag. Required, and unique within a document.</summary>
    public string Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value ?? ""))
            {
                Touch();
            }
        }
    }

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

    public string Location
    {
        get => _location;
        set
        {
            if (SetProperty(ref _location, value ?? ""))
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

    /// <summary>
    /// Recomputes the roll-up columns shown in the container list.
    /// </summary>
    /// <remarks>
    /// Called on load, add and remove rather than on every quantity keystroke: recomputing is
    /// cheap, but the notification is what costs, and firing it per keystroke makes the list
    /// flicker while a number is being typed.
    /// </remarks>
    public void RefreshRollups()
    {
        OnPropertyChanged(nameof(TotalQuantity));
        OnPropertyChanged(nameof(ItemsNeedingIdentity));
    }

    private void AddItem()
    {
        var item = new ItemViewModel(new Item { Name = "New item", Quantity = 1 });
        item.EnsureIdentity();

        Items.Add(item);
        RefreshRollups();
    }

    private void RemoveItem(object? parameter)
    {
        // The parameter is the row the delete key was pressed on rather than the selection:
        // leaning on selection makes the button feel unreliable the moment the grid is filtered.
        if (parameter is not ItemViewModel item)
        {
            return;
        }

        Items.Remove(item);
        RefreshRollups();
    }

    private bool CanRemoveItem(object? parameter) => parameter is ItemViewModel;

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        OnPropertyChanged(nameof(UpdatedAt));
    }

    /// <summary>Projects this view back to an immutable <see cref="Container"/>.</summary>
    public Container ToContainer()
    {
        // Every row is given an identity on the way out. Minting here rather than on edit means
        // an untouched legacy row still leaves the desktop merge-safe instead of quietly
        // matching another row by name.
        foreach (var item in Items)
        {
            item.EnsureIdentity();
        }

        return new Container
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Location = Location.Trim(),
            Notes = Notes.Trim(),
            UpdatedAt = UpdatedAt,
            Items = Items.Select(i => i.ToItem()).ToList(),
        };
    }

    public override string ToString() => string.IsNullOrWhiteSpace(_id) ? "(untagged)" : _id;
}
