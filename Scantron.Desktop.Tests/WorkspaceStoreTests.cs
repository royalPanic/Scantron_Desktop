using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Asserts that the desktop's working state survives a restart intact.
/// </summary>
/// <remarks>
/// The base snapshot is the input the three-way merge cannot work without. If it were dropped,
/// every later merge would quietly behave like a first sync and stop being able to tell a
/// deliberate edit from an untouched row - so these cases treat its preservation as the
/// contract, not an implementation detail.
/// </remarks>
public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _directory;

    public WorkspaceStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scantron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static InventoryDocument Doc() => new()
    {
        Containers =
        [
            new Container
            {
                Id = "BOX-101",
                Name = "Shelf stock",
                Location = "Shelf 2-A",
                UpdatedAt = 1_700_000_000_000,
                Items = [new Item { Uuid = "u1", Name = "Drill", Quantity = 4, Barcode = "12345" }],
            },
        ],
    };

    [Fact]
    public void A_missing_workspace_is_a_normal_first_run()
    {
        var store = new WorkspaceStore(_directory);

        var loaded = store.TryLoad(out var error);

        Assert.False(loaded);
        Assert.Null(error);
        Assert.Null(store.Current);
        Assert.Null(store.Base);
    }

    [Fact]
    public void Current_and_base_survive_a_restart()
    {
        var store = new WorkspaceStore(_directory);
        store.SetDocuments(Doc(), Doc());
        Assert.Null(store.Save());

        var reopened = new WorkspaceStore(_directory);
        Assert.True(reopened.TryLoad(out var error), error);

        Assert.Equal("BOX-101", reopened.Current!.Containers[0].Id);
        Assert.Equal(4, reopened.Current.Containers[0].Items[0].Quantity);
        Assert.NotNull(reopened.Base);
        Assert.Equal("BOX-101", reopened.Base!.Containers[0].Id);
    }

    [Fact]
    public void An_absent_base_is_reported_as_absent_rather_than_invented()
    {
        var store = new WorkspaceStore(_directory);
        store.SetDocuments(Doc(), null);
        store.Save();

        var reopened = new WorkspaceStore(_directory);
        reopened.TryLoad(out _);

        // A first sync has no base. Fabricating an empty one would report every row as a
        // conflict; silently reusing current would report none, and neither is the truth.
        Assert.Null(reopened.Base);
        Assert.NotNull(reopened.Current);
    }

    [Fact]
    public void Promoting_a_base_does_not_touch_the_current_document()
    {
        var store = new WorkspaceStore(_directory);
        store.SetDocuments(Doc(), null);
        var promoted = Doc() with
        {
            Containers = [Doc().Containers[0] with { Id = "BOX-999" }],
        };

        store.PromoteBase(promoted);

        Assert.Equal("BOX-101", store.Current!.Containers[0].Id);
        Assert.Equal("BOX-999", store.Base!.Containers[0].Id);
    }

    [Fact]
    public void A_corrupt_workspace_is_reported_and_left_on_disk()
    {
        var path = Path.Combine(_directory, "workspace.current.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new WorkspaceStore(_directory);
        store.TryLoad(out var error);

        Assert.NotNull(error);
        Assert.Contains("workspace.current.json", error!, StringComparison.Ordinal);

        // The file must survive: an operator may still salvage an export from it, and a tool
        // that deletes unreadable inventory is worse than one that complains.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_stored_workspace_is_readable_by_the_device_reader()
    {
        var store = new WorkspaceStore(_directory);
        store.SetDocuments(Doc(), Doc());
        store.Save();

        // Written in the export format on purpose: an operator can inspect either file with a
        // text editor, and it cannot drift from what an export produces.
        var parsed = InventoryReader.Read(File.ReadAllText(store.CurrentPath));

        Assert.True(InventoryValidator.Validate(parsed).IsValid);
    }
}
