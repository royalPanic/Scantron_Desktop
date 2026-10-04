using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Scantron.Desktop.Services.Transfer;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Asserts that an inbound push lands on disk intact, and that the inbox cannot grow without
/// bound.
/// </summary>
/// <remarks>
/// The staged copy is the desktop's last line of defence: it is what is there if a merge goes
/// wrong, or if the operator wants to see exactly what the handheld sent. A truncated file
/// would be worse than none, because it looks like data until it is opened.
/// </remarks>
public sealed class InboxStoreTests : IDisposable
{
    private readonly string _directory;

    public InboxStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "scantron-inbox-tests", Guid.NewGuid().ToString("N"));
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

    private static InventoryDocument Doc(string id = "BOX-101") => new()
    {
        Containers =
        [
            new Container
            {
                Id = id,
                Name = "Shelf stock",
                Items = [new Item { Uuid = "u1", Name = "Drill", Quantity = 4, UpdatedAt = 1_700_000_000_000 }],
            },
        ],
    };

    [Fact]
    public void A_staged_push_is_re_openable_by_the_device_reader()
    {
        var store = new InboxStore(_directory);

        var error = store.Stage(Doc(), out var path);

        Assert.Null(error);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var parsed = InventoryReader.Read(File.ReadAllText(path!));
        Assert.Equal("BOX-101", parsed.Containers[0].Id);
        Assert.Equal(4, parsed.Containers[0].Items[0].Quantity);
        Assert.True(InventoryValidator.Validate(parsed).IsValid);
    }

    [Fact]
    public void Staging_leaves_no_temporary_file_behind()
    {
        var store = new InboxStore(_directory);

        store.Stage(Doc(), out _);

        // The temporary and the target share a directory precisely so the final step is a
        // rename. A stray .tmp means the move did not happen and the file is not atomic.
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
    }

    [Fact]
    public void Two_pushes_in_the_same_second_do_not_overwrite_each_other()
    {
        var store = new InboxStore(_directory);

        var first = store.Stage(Doc("BOX-1"), out var firstPath);
        var second = store.Stage(Doc("BOX-2"), out var secondPath);

        Assert.Null(first);
        Assert.Null(second);
        Assert.NotEqual(firstPath, secondPath);

        // A CK65 finishing one sync the moment the last one landed is ordinary, and silently
        // losing the first of the two is not an acceptable way to lose it.
        Assert.Equal("BOX-1", InventoryReader.Read(File.ReadAllText(firstPath!)).Containers[0].Id);
        Assert.Equal("BOX-2", InventoryReader.Read(File.ReadAllText(secondPath!)).Containers[0].Id);
    }

    [Fact]
    public void The_inbox_keeps_the_newest_twenty_and_prunes_the_rest()
    {
        var store = new InboxStore(_directory);
        for (var i = 0; i < InboxStore.Retention + 5; i++)
        {
            Assert.Null(store.Stage(Doc($"BOX-{i}"), out _));
        }

        var staged = Directory.GetFiles(store.Directory, "push-*.json");
        Assert.Equal(InboxStore.Retention, staged.Length);

                // Ordered by name, so the newest is last. Pruning the wrong end would delete the push
                // that just arrived and keep stale ones forever.
                Assert.Equal("BOX-24", IdIn(staged[^1]));
                Assert.Equal("BOX-5", IdIn(staged[0]));
            }

            /// <summary>Reads one staged file's single container tag.</summary>
            private static string IdIn(string path) =>
                InventoryReader.Read(File.ReadAllText(path)).Containers[0].Id;

    [Fact]
    public void Pruning_removes_leftover_temporaries_too()
    {
        var store = new InboxStore(_directory);
        Directory.CreateDirectory(store.Directory);

        // A crash between writing the temporary and moving it into place.
        File.WriteAllText(Path.Combine(store.Directory, "push-20260101-000000-001.json.tmp"), "{");

        store.Stage(Doc(), out _);

        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
    }

    [Fact]
    public void The_inbox_directory_is_named_for_where_it_lives()
    {
        var store = new InboxStore(_directory);

        Assert.Equal(Path.Combine(_directory, "inbox"), store.Directory);
        Assert.False(Directory.Exists(store.Directory));
    }
}