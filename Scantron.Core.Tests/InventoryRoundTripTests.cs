using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Round-trips a committed export through the reader and writer and asserts the result is
/// <em>semantically</em> identical to the input.
/// </summary>
/// <remarks>
/// Semantic, not byte, equality is the correct assertion: key ordering and indentation are
/// presentation details that legitimately differ between the Android writer and this one, and
/// pinning them would make the test fail for changes that break nothing. What must hold is that
/// every field a merge depends on survives the trip.
/// </remarks>
public sealed class InventoryRoundTripTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "scantron_inventory.json");

    private static string ReadFixture() => File.ReadAllText(FixturePath);

    [Fact]
    public void Fixture_is_copied_to_the_test_output()
    {
        Assert.True(File.Exists(FixturePath), $"fixture missing at {FixturePath}");
    }

    [Fact]
    public void Fixture_parses_and_passes_validation()
    {
        var document = InventoryReader.Read(ReadFixture());

        var validation = InventoryValidator.Validate(document);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void Round_trip_preserves_every_container_and_item()
    {
        var original = InventoryReader.Read(ReadFixture());
        var reparsed = InventoryReader.Read(InventoryReader.Write(original));

        Assert.Equal(original.Containers.Count, reparsed.Containers.Count);

        foreach (var (before, after) in original.Containers.Zip(reparsed.Containers))
        {
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.Location, after.Location);
            Assert.Equal(before.Notes, after.Notes);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(before.Items.Count, after.Items.Count);

            foreach (var (b, a) in before.Items.Zip(after.Items))
            {
                Assert.Equal(b.Uuid, a.Uuid);
                Assert.Equal(b.Name, a.Name);
                Assert.Equal(b.Barcode, a.Barcode);
                Assert.Equal(b.Quantity, a.Quantity);
                Assert.Equal(b.Category, a.Category);
                Assert.Equal(b.Notes, a.Notes);
                Assert.Equal(b.UpdatedAt, a.UpdatedAt);
            }
        }
    }

    [Fact]
    public void Round_trip_is_idempotent()
    {
        var original = InventoryReader.Read(ReadFixture());
        var once = InventoryReader.Write(original);
        var twice = InventoryReader.Write(InventoryReader.Read(once));

        // Second pass must be byte-identical to the first: writing is a fixed point once the
        // document is normalised, so repeated sync cycles cannot drift the file.
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Written_document_uses_the_current_version()
    {
        var original = InventoryReader.Read(ReadFixture());
        var written = InventoryReader.Read(InventoryReader.Write(original));

        Assert.Equal(InventoryFormat.CurrentVersion, written.Version);
        Assert.Equal(InventoryFormat.AppId, written.App);
    }

    [Fact]
    public void An_empty_items_array_survives_as_an_empty_container()
    {
        var original = InventoryReader.Read(ReadFixture());
        var reparsed = InventoryReader.Read(InventoryReader.Write(original));

        var empty = reparsed.Containers.Single(c => c.Id == "EMPTY-1");
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void An_absent_uuid_survives_as_empty_rather_than_null()
    {
        var reparsed = InventoryReader.Read(InventoryReader.Write(InventoryReader.Read(ReadFixture())));

        var item = reparsed.Containers
            .SelectMany(c => c.Items)
            .Single(i => i.Name == "Unlabelled spare");

        Assert.Equal("", item.Uuid);
    }
}