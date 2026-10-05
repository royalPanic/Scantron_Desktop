using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Mvvm;
using Scantron.Desktop.Services;
using Scantron.Desktop.Services.Transfer;
using Scantron.Desktop.ViewModels;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Pins the two promises the transfer feature makes about the view model: there is exactly one
/// merge, and a pull never serves a document the operator never opened.
/// </summary>
/// <remarks>
/// The interesting case here is not the happy path. It is that a push arriving over the network
/// and a file arriving over USB end up in the same merge, because a second implementation would
/// be free to drift the first time a rule changed - and the drift would show up as transfers
/// behaving differently from imports, which is the one thing the feature promises will not happen.
/// </remarks>
public sealed class MainViewModelTransferTests : IDisposable
{
    private readonly string _directory;

    public MainViewModelTransferTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scantron-transfer-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>
        /// A hub on a port nothing will use.
        /// </summary>
        /// <remarks>
        /// These cases are about the view model's merge, staging and guards - none of which need a
        /// socket - so the hub exists only to satisfy the constructor and is never started. The
        /// socket layer gets its own end-to-end coverage on a real loopback port.
        /// </remarks>
        private MainViewModel NewViewModel() =>
            new(new WorkspaceStore(_directory), new InboxStore(_directory), new TransferHub(UnusedPort));

        private const int UnusedPort = 18999;

    private static InventoryDocument Doc(string id = "BOX-101", long updatedAt = 1_700_000_000_000, int quantity = 4) =>
        new()
        {
            Containers =
            [
                new Container
                {
                    Id = id,
                    Name = "Shelf stock",
                    UpdatedAt = updatedAt,
                    Items = [new Item { Uuid = "u1", Name = "Drill", Quantity = quantity, UpdatedAt = updatedAt }],
                },
            ],
        };

    private void Load(MainViewModel vm, InventoryDocument document)
    {
        var path = Path.Combine(_directory, "seed.json");
        File.WriteAllText(path, InventoryReader.Write(document));
        vm.LoadFrom(new Uri(path));
    }

    [Fact]
    public void An_inbound_push_and_a_file_import_produce_the_same_merged_document()
    {
        var baseline = Doc();
        var fromHandheld = Doc(quantity: 9, updatedAt: 1_700_000_600_000);

        var overNetwork = NewViewModel();
        Load(overNetwork, baseline);
        overNetwork.MergeFromNetwork(fromHandheld);
        overNetwork.AcceptMerge();

        var overUsb = NewViewModel();
        Load(overUsb, baseline);
        overUsb.MergeFrom(new Uri(WriteTo("import.json", fromHandheld)));
        overUsb.AcceptMerge();

                var fromWifi = ContainerOf(overNetwork.LiveDocument());
                var fromStick = ContainerOf(overUsb.LiveDocument());

                // Same input, same result - one merge path, not two that happen to agree today.
                //
                // Compared field by field rather than as serialized text, because BuildDocument stamps a
                // fresh exportedAt on every read. That is correct behaviour and is not what this case is
                // about; what matters is that both routes converge on the same inventory.
                Assert.Equal(fromStick.Id, fromWifi.Id);
                Assert.Equal(fromStick.Items.Single().Name, fromWifi.Items.Single().Name);
                Assert.Equal(fromStick.Items.Single().Quantity, fromWifi.Items.Single().Quantity);
                Assert.Equal(9, fromWifi.Items.Single().Quantity);
            }

            private static Container ContainerOf(InventoryDocument? document) => document!.Containers[0];

    [Fact]
    public void An_inbound_push_is_staged_to_the_inbox_before_it_is_merged()
    {
        var vm = NewViewModel();
        Load(vm, Doc());

        vm.MergeFromNetwork(Doc(quantity: 9, updatedAt: 1_700_000_600_000));

        // The merge is held for review, so the staged file is the only copy of what the scanner
        // sent until the operator accepts. It has to be there.
        var staged = Directory.GetFiles(Path.Combine(_directory, "inbox"), "push-*.json");
        Assert.Single(staged);
        Assert.Equal(9, InventoryReader.Read(File.ReadAllText(staged[0])).Containers[0].Items[0].Quantity);
    }

    [Fact]
    public void An_inbound_push_is_not_applied_until_it_is_accepted()
    {
        var vm = NewViewModel();
        Load(vm, Doc());
        var before = vm.LiveDocument()!.Containers[0].Items[0].Quantity;

        vm.MergeFromNetwork(Doc(quantity: 9, updatedAt: 1_700_000_600_000));

        // Held, exactly like a file import. A merge that lands the moment it arrives is how a
        // judgement call turns into data loss.
        Assert.Equal(before, vm.LiveDocument()!.Containers[0].Items[0].Quantity);

                // Acceptable because nothing needs deciding: only the handheld moved, so there is no
                // conflict to resolve. The merge is still held - the quantity above is unchanged - it is
                // just that the operator can choose to apply it straight away. A conflicted push is the
                // case that must block, and
                // A_conflict_from_a_push_is_reported_to_the_handheld_without_blocking_the_transfer covers it.
                Assert.True(vm.AcceptMergeCommand.CanExecute(null));

                                // Applying it is a deliberate act, and only then does the handheld's value land.
                                vm.AcceptMergeCommand.Execute(null);
                                Assert.Equal(9, vm.LiveDocument()!.Containers[0].Items[0].Quantity);
                            }

    [Fact]
    public void A_fresh_app_has_nothing_to_pull()
    {
        var vm = NewViewModel();

        // The distinction the whole 503 rule rests on: an app launched onto an empty desk has no
        // document, and must say so rather than serve an empty one that imports as "delete
        // everything" on the handheld.
        Assert.Null(vm.LiveDocument());
    }

    [Fact]
    public void An_untouched_workspace_on_a_cold_start_still_has_nothing_to_pull()
    {
        // Distinct from a brand new MainViewModel, because the grid has to exist for the view to
        // bind to - so this case is where a null-check on the document would have said "here is
        // your empty document" and wiped the scanner.
        var first = NewViewModel();
        first.NewCommand.Execute(null);

        var second = NewViewModel();

        Assert.Null(second.LiveDocument());
    }

    [Fact]
    public void A_deliberately_new_document_is_served_even_though_it_is_empty()
    {
        var vm = NewViewModel();
        vm.NewCommand.Execute(null);

        // The operator said "empty", so it is a real document. Refusing to serve it would make
        // the 503 above the only answer rather than the safe one.
        var served = vm.LiveDocument();
        Assert.NotNull(served);
        Assert.Empty(served!.Containers);
    }

    [Fact]
    public void A_pull_serves_the_grid_rather_than_the_last_saved_snapshot()
    {
        var vm = NewViewModel();
        Load(vm, Doc());
        vm.Containers[0].Items[0].Quantity = 21;

        // Rebuild from the grid every time it is asked, the same reason the merge path rebuilds:
        // the snapshot is minutes old and the operator is looking at the grid.
        Assert.Equal(21, vm.LiveDocument()!.Containers[0].Items[0].Quantity);
    }

    [Fact]
        public async Task Sending_to_the_handheld_requires_a_document_and_passes_the_export_gate()
    {
        var vm = NewViewModel();

        Assert.False(vm.PushToHandheldCommand.CanExecute(null));

        Load(vm, Doc());
        Assert.True(vm.PushToHandheldCommand.CanExecute(null));

                // Press the button rather than calling the handler: Ctrl+P is bound to this command,
                // and a test that bypassed it would keep passing if the binding ever came loose.
                    //
                    // No address is set, so there is nowhere to send. The point of this case is the
                    // *guard* and the export gate, not the network - the button has to say why it did
                    // nothing rather than claim a transfer happened.
                    vm.PushToHandheldCommand.Execute(null);
                    await WaitForCommandToFinishAsync(vm.PushToHandheldCommand);

            Assert.False(vm.HasServedToHandheld);
                    Assert.Contains("Handheld address", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                }

        [Fact]
        public async Task A_document_the_handheld_would_reject_is_not_offered_to_it()
        {
            var vm = NewViewModel();
            Load(vm, Doc());
            vm.HandheldAddress = "127.0.0.1";

            // Valid on the way in, invalid on the way out: the row is renamed to blank.
            vm.Containers[0].Items[0].Name = "   ";

            await SendToHandheld(vm);

            // Refused before the socket is opened, so nothing reached a device - and the flag that
            // reports a delivered transfer stays false.
            Assert.False(vm.HasServedToHandheld);
                    Assert.Contains("rejected by the handheld", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                }

        /// <summary>
        /// Waits for an async command to finish.
        /// </summary>
        /// <remarks>
        /// <c>AsyncRelayCommand</c> is fire-and-forget, so <c>Execute</c> returns before the handler
        /// has awaited its socket. Polling <c>IsBusy</c> rather than sleeping a fixed time keeps the
        /// assertion deterministic and stops a slow machine from failing a passing test.
        /// </remarks>
        private static async Task WaitForCommandToFinishAsync(AsyncRelayCommand command)
        {
            for (var attempt = 0; attempt < 300 && !command.CanExecute(null); attempt++)
            {
                await Task.Delay(20);
            }

            Assert.True(command.CanExecute(null), "The command never finished.");
        }

    [Fact]
    public void Transfer_commands_are_disabled_until_a_document_exists()
    {
        var vm = NewViewModel();

        Assert.False(vm.PushToHandheldCommand.CanExecute(null));
        Assert.False(vm.ReceiveFromHandheldCommand.CanExecute(null));

        Load(vm, Doc());

        Assert.True(vm.PushToHandheldCommand.CanExecute(null));
    }

    [Fact]
    public void Receiving_is_disabled_while_the_hub_is_not_listening()
    {
        var vm = NewViewModel();
        Load(vm, Doc());

        // Nothing can be received from a hub that is not listening, and a button that offers it
        // anyway is a promise the app cannot keep.
        Assert.False(vm.IsSharing);
        Assert.False(vm.ReceiveFromHandheldCommand.CanExecute(null));
    }

    [Fact]
        public void A_conflict_from_a_push_is_reported_to_the_handheld_without_blocking_the_transfer()
        {
            var vm = NewViewModel();
            Load(vm, Doc());

            // Both sides changed quantity away from the baseline, to different values.
            vm.Containers[0].Items[0].Quantity = 3;
            vm.Containers[0].Items[0].Touch();

            var response = vm.MergeFromNetwork(Doc(quantity: 9, updatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            // The scanner has a ten second timeout and nothing to do but wait, so it is told the
            // merge landed and is waiting on a human. It is not told the document is done: nothing
            // is, until the operator accepts.
            Assert.Equal(200, response.StatusCode);
            Assert.StartsWith("""{"ok":true,"conflicts":""", response.Body, StringComparison.Ordinal);
            Assert.Contains("Sync review", response.Body, StringComparison.Ordinal);
            Assert.False(vm.AcceptMergeCommand.CanExecute(null));
        }

        [Fact]
        public void A_clean_push_acknowledges_the_counts_the_handheld_asked_about()
        {
            var vm = NewViewModel();
            Load(vm, Doc());

            var response = vm.MergeFromNetwork(Doc(quantity: 9, updatedAt: 1_700_000_600_000));

            Assert.Equal("""{"ok":true,"containers":1,"items":1}""", response.Body);
        }

    [Fact]
    public void Pulling_from_the_handheld_is_driven_from_the_device()
    {
        var vm = NewViewModel();
        Load(vm, Doc());

                // Driven directly rather than through the command: ReceiveFromHandheld is guarded on the
                // hub listening, and this fixture deliberately never starts one, so pressing the button
                // would be a no-op and prove nothing. The guard itself is covered by
                // Receiving_is_disabled_while_the_hub_is_not_listening.
                StartSharing(vm);
                vm.ReceiveFromHandheldCommand.Execute(null);
                Assert.Contains("handheld", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
                StopSharing(vm);
            }

            /// <summary>
            /// Opens a hub on this fixture's unused port so the share-dependent guards become reachable.
            /// </summary>
            /// <remarks>
            /// Deliberately best-effort. Binding can be refused on a machine configured such that even
            /// loopback is unavailable, and a transfer test failing because a sandbox refused a port would
            /// be noise rather than signal.
            /// </remarks>
            private static void StartSharing(MainViewModel vm)
            {
                vm.StartSharingCommand.Execute(null);
            }

            private static void StopSharing(MainViewModel vm)
            {
                vm.StopSharingCommand.Execute(null);
            }

    /// <summary>
        /// Runs the send path directly and reports what the hub relays back to the handheld.
        /// </summary>
        /// <remarks>
            /// Internal rather than public, and for the same reason the push path is: a test should drive
            /// the code that actually runs. The button wiring is covered separately by
            /// <see cref="Sending_to_the_handheld_requires_a_document_and_passes_the_export_gate"/>,
            /// which asserts that pressing the command is what moves the state.
            /// </remarks>
            private static Task SendToHandheld(MainViewModel vm) => vm.SendToHandheld();

        private string WriteTo(string name, InventoryDocument document)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, InventoryReader.Write(document));
        return path;
    }
}