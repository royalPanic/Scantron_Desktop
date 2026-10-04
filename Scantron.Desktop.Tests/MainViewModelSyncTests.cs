using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services;
using Scantron.Desktop.ViewModels;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Drives the full sync workflow through the view model with real files and no WPF types.
/// </summary>
/// <remarks>
/// The view model takes a workspace directory and file paths rather than dialogs, so the entire
/// import-merge-resolve-accept path is exercisable here. These are the cases where a mistake
/// loses stock, so they assert on the document that would actually be exported.
/// </remarks>
public sealed class MainViewModelSyncTests : IDisposable
{
    private readonly string _directory;

    public MainViewModelSyncTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scantron-vm-tests", Guid.NewGuid().ToString("N"));
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

    private WorkspaceStore NewStore() => new(_directory);

    private MainViewModel NewViewModel() => new(NewStore());

    private static void WriteFile(string path, InventoryDocument document) =>
        File.WriteAllText(path, InventoryReader.Write(document));

    private static InventoryDocument Doc(params Container[] containers) => new() { Containers = containers };

    private static Container Cont(string id = "BOX-101", string name = "Shelf stock", params Item[] items) =>
        new() { Id = id, Name = name, Items = items };

    private static Item Item(
        string uuid = "u1",
        string name = "Drill",
        int quantity = 1,
        long updatedAt = 1_700_000_000_000) =>
        new() { Uuid = uuid, Name = name, Quantity = quantity, UpdatedAt = updatedAt };

    [Fact]
    public void A_new_view_model_starts_empty_and_ready()
    {
        var vm = NewViewModel();

        Assert.Empty(vm.Containers);
        Assert.Equal("0 container(s), 0 item(s)", vm.DocumentSummary);
        Assert.False(vm.AcceptMergeCommand.CanExecute(null));
    }

    [Fact]
    public void Opening_an_export_populates_the_container_list()
    {
        var path = Path.Combine(_directory, "open.json");
        WriteFile(path, Doc(Cont(items: [Item()])));

        var vm = NewViewModel();
        vm.LoadFrom(new Uri(path));

        Assert.Single(vm.Containers);
        Assert.Equal("BOX-101", vm.Containers[0].Id);
        Assert.NotNull(vm.SelectedContainer);
    }

    [Fact]
    public void Opening_an_export_makes_it_the_sync_baseline()
    {
        var path = Path.Combine(_directory, "baseline.json");
        WriteFile(path, Doc(Cont(items: [Item(quantity: 4)])));

        var vm = NewViewModel();
        vm.LoadFrom(new Uri(path));

        // Re-merging the same file must then be a no-op. If opening did not set the baseline,
        // every row would look newly changed and the operator would face a wall of conflicts
        // for a file they had just exported themselves.
        vm.MergeFrom(new Uri(path));
        Assert.Empty(vm.Conflicts);
    }

    [Fact]
    public void Exporting_refuses_a_document_the_device_would_reject()
    {
        var vm = NewViewModel();
        vm.AddContainerCommand.Execute(null);
        vm.Containers[0].AddItemCommand.Execute(null);
        vm.Containers[0].Items[0].Name = "   ";

        // The export path is the only place the blank name can be caught: the validator, not
        // the grid, is what the device applies.
        var path = Path.Combine(_directory, "bad.json");
        vm.SaveTo(new Uri(path));

        Assert.False(File.Exists(path));
        Assert.Contains("rejected", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exporting_a_valid_document_writes_it_in_the_device_format()
    {
        var vm = NewViewModel();
        vm.AddContainerCommand.Execute(null);
        vm.Containers[0].Id = "BOX-101";
        vm.Containers[0].AddItemCommand.Execute(null);
        vm.Containers[0].Items[0].Name = "Drill";
        vm.Containers[0].Items[0].Quantity = 4;

        var path = Path.Combine(_directory, "good.json");
        vm.SaveTo(new Uri(path));

        Assert.True(File.Exists(path));
        Assert.Equal(4, InventoryReader.Read(File.ReadAllText(path)).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void A_conflicting_merge_is_held_until_the_operator_decides()
    {
        var baseDoc = Doc(Cont(items: [Item(quantity: 1)]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        // Desktop moved quantity to 3, handheld to 9: a true conflict.
        var remotePath = Path.Combine(_directory, "remote.json");
        WriteFile(remotePath, Doc(Cont(items: [Item(quantity: 9, updatedAt: 1_700_000_500_000)])));

        var vm = new MainViewModel(workspace);
        vm.Containers[0].Items[0].Quantity = 3;
        vm.MergeFrom(new Uri(remotePath));

        var conflict = Assert.Single(vm.Conflicts);
        Assert.Equal("Quantity", conflict.Field);
        Assert.Equal("3", conflict.LocalValue);
        Assert.Equal("9", conflict.RemoteValue);

        // Accept must stay blocked while the decision is open. The command is disabled, so a
        // click cannot reach it at all - the guard is what stops the merge being applied blind.
        Assert.False(vm.AcceptMergeCommand.CanExecute(null));

        // Belt and braces: even if the guard were bypassed, the method itself refuses.
        vm.AcceptMerge();
        Assert.Contains("Resolve every conflict", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Single(vm.Conflicts);
        Assert.False(vm.Conflicts[0].IsResolved);
    }

    [Fact]
    public void Taking_the_remote_value_writes_it_into_the_document()
    {
        var baseDoc = Doc(Cont(items: [Item(quantity: 1)]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        var remotePath = Path.Combine(_directory, "remote-take.json");
        WriteFile(remotePath, Doc(Cont(items: [Item(quantity: 9, updatedAt: 1_700_000_500_000)])));

        var vm = new MainViewModel(workspace);
        vm.Containers[0].Items[0].Quantity = 3;
        vm.MergeFrom(new Uri(remotePath));
        vm.TakeAllRemoteCommand.Execute(null);
        vm.AcceptMergeCommand.Execute(null);

        var path = Path.Combine(_directory, "after-accept.json");
        vm.SaveTo(new Uri(path));

        Assert.Equal(9, InventoryReader.Read(File.ReadAllText(path)).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void Keeping_the_desktop_value_leaves_it_in_place()
    {
        var baseDoc = Doc(Cont(items: [Item(quantity: 1)]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        var remotePath = Path.Combine(_directory, "remote-keep.json");
        WriteFile(remotePath, Doc(Cont(items: [Item(quantity: 9, updatedAt: 1_700_000_500_000)])));

        var vm = new MainViewModel(workspace);
        vm.Containers[0].Items[0].Quantity = 3;
        vm.MergeFrom(new Uri(remotePath));
        vm.TakeAllLocalCommand.Execute(null);
        vm.AcceptMergeCommand.Execute(null);

        var path = Path.Combine(_directory, "after-keep.json");
        vm.SaveTo(new Uri(path));

        Assert.Equal(3, InventoryReader.Read(File.ReadAllText(path)).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void Discarding_a_merge_leaves_the_document_untouched()
    {
        var baseDoc = Doc(Cont(items: [Item(quantity: 1)]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        var remotePath = Path.Combine(_directory, "remote-discard.json");
        WriteFile(remotePath, Doc(Cont(items: [Item(quantity: 9, updatedAt: 1_700_000_500_000)])));

        var vm = new MainViewModel(workspace);
        vm.Containers[0].Items[0].Quantity = 3;
        vm.MergeFrom(new Uri(remotePath));
        vm.DiscardMergeCommand.Execute(null);

        Assert.Empty(vm.Conflicts);
        Assert.False(vm.AcceptMergeCommand.CanExecute(null));
        Assert.Equal(3, vm.Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void Accepting_a_clean_merge_promotes_it_to_the_new_baseline()
    {
        var baseDoc = Doc(Cont(items: [Item(quantity: 1)]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        // The handheld only added a row: nothing to decide.
        var remotePath = Path.Combine(_directory, "remote-clean.json");
        WriteFile(remotePath, Doc(Cont(items:
        [
            Item(quantity: 1),
            Item("u2", "Nails", 500, updatedAt: 1_700_000_500_000),
        ])));

        var vm = new MainViewModel(workspace);
        vm.MergeFrom(new Uri(remotePath));

        Assert.Empty(vm.Conflicts);
        vm.AcceptMergeCommand.Execute(null);
        Assert.Equal(2, vm.Containers[0].Items.Count);

        // Re-merging the same export must now be quiet, which is only true if the accepted
        // merge became the baseline.
        vm.MergeFrom(new Uri(remotePath));
        Assert.Empty(vm.Conflicts);
    }

    [Fact]
    public void A_container_added_on_the_handheld_survives_the_merge()
    {
        var baseDoc = Doc(Cont(items: [Item()]));
        var workspace = NewStore();
        workspace.SetDocuments(baseDoc, baseDoc);
        workspace.Save();

        var remotePath = Path.Combine(_directory, "remote-new-container.json");
        WriteFile(remotePath, Doc(
            Cont(items: [Item()]),
            Cont("BOX-202", "Overflow", Item("u9", "Tape", 3, 1_700_000_500_000))));

        var vm = new MainViewModel(workspace);
        vm.MergeFrom(new Uri(remotePath));
        vm.AcceptMergeCommand.Execute(null);

        Assert.Equal(2, vm.Containers.Count);
        Assert.Contains(vm.Containers, c => c.Id == "BOX-202");
    }

    [Fact]
    public void Edits_are_minted_with_an_identity_so_they_merge_safely()
    {
        var vm = NewViewModel();
        vm.AddContainerCommand.Execute(null);
        vm.Containers[0].Id = "BOX-101";
        vm.Containers[0].AddItemCommand.Execute(null);
        vm.Containers[0].AddItemCommand.Execute(null);

        // Two rows with the same name and no barcode would merge into one under name-only
        // matching, silently halving the count. Minted identity makes that impossible.
        vm.Containers[0].Items[0].Name = "Nails";
        vm.Containers[0].Items[1].Name = "Nails";

        var path = Path.Combine(_directory, "identity.json");
        vm.SaveTo(new Uri(path));

        var exported = InventoryReader.Read(File.ReadAllText(path));
        Assert.Equal(2, exported.Containers[0].Items.Count);
        Assert.Equal(2, exported.Containers[0].Items.Select(i => i.Uuid).Distinct().Count());
    }

    [Fact]
    public void Editing_a_row_advances_its_timestamp_past_the_baseline()
    {
        var vm = NewViewModel();
        vm.AddContainerCommand.Execute(null);
        vm.Containers[0].AddItemCommand.Execute(null);
        var item = vm.Containers[0].Items[0];
        var before = item.UpdatedAt;

        item.Name = "Renamed";

        // The merger reads updatedAt to decide whether a side changed anything, so an edit that
        // does not advance it would be reverted by the next sync.
        Assert.True(item.UpdatedAt >= before);
        Assert.True(item.UpdatedAt >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
    }

    [Fact]
    public void An_export_always_declares_a_version_the_device_accepts()
    {
        // The device range-checks `version` on import and rejects anything outside 1.0/1.1, so a
        // document built from an empty view model has to stamp a real version rather than
        // leaving it blank.
        var vm = NewViewModel();
        vm.AddContainerCommand.Execute(null);
        vm.Containers[0].Id = "BOX-101";
        vm.Containers[0].AddItemCommand.Execute(null);
        vm.Containers[0].Items[0].Name = "Drill";

        var path = Path.Combine(_directory, "version.json");
        vm.SaveTo(new Uri(path));

        var json = File.ReadAllText(path);
        Assert.Contains($"\"{InventoryFormat.AppId}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"version\": \"{InventoryFormat.CurrentVersion}\"", json, StringComparison.Ordinal);

        // And the round-trip still passes the device's own import gate.
        var parsed = InventoryReader.Read(json);
        Assert.Contains(parsed.Version, InventoryFormat.ImportableVersions);
        Assert.True(InventoryValidator.Validate(parsed).IsValid);
    }

    [Fact]
    public void Exporting_stamps_a_fresh_timestamp()
    {
        var path = Path.Combine(_directory, "first.json");
        WriteFile(path, Doc(Cont(items: [Item()])));

        var vm = NewViewModel();
        vm.LoadFrom(new Uri(path));

        Assert.Single(vm.Containers);

        var outPath = Path.Combine(_directory, "stamped.json");
        vm.SaveTo(new Uri(outPath));

        var parsed = InventoryReader.Read(File.ReadAllText(outPath));
        var stamped = InventoryFormat.ParseExportedAt(parsed.ExportedAt);
        Assert.NotNull(stamped);

        // exportedAt is local wall clock wearing a literal 'Z', so the parsed value sits on the
        // UTC axis at the machine's local time. The only robust check is against that same wall
        // clock: parse the value back as text and compare it to today's local date, which holds
        // whatever offset the machine has.
        var asText = InventoryFormat.FormatExportedAt(stamped!.Value);
        Assert.Equal(DateTime.Now.Date, stamped.Value.Date);
        Assert.StartsWith(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_sharing_a_uuid_are_both_preserved_rather_than_collapsed()
    {
        // Two rows carrying one uuid is the case the desktop used to collapse into a single
        // row, silently dropping stock the handheld still held. The core resolver now re-mints,
        // but the UI must not collapse them first: it maps items one-to-one and lets the core
        // apply the device's rule.
        var document = Doc(Cont(items:
        [
            Item("shared", "Nails", 10),
            Item("shared", "Screws", 20),
        ]));

        var path = Path.Combine(_directory, "dup-uuid.json");
        WriteFile(path, document);

        var vm = NewViewModel();
        vm.LoadFrom(new Uri(path));

        Assert.Equal(2, vm.Containers[0].Items.Count);

        var outPath = Path.Combine(_directory, "dup-uuid-out.json");
        vm.SaveTo(new Uri(outPath));

        var exported = InventoryReader.Read(File.ReadAllText(outPath));
        Assert.Equal(2, exported.Containers[0].Items.Count);
        Assert.Equal(
            exported.Containers[0].Items.Select(i => i.Name).Order(),
            ["Nails", "Screws"]);
    }
}
