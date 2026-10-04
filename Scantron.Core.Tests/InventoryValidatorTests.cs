using Scantron.Core.Models;
using Scantron.Core.Serialization;
using Xunit;

namespace Scantron.Core.Tests;

/// <summary>
/// Asserts that the validator rejects exactly what the Android importer rejects.
/// </summary>
/// <remarks>
/// These cases mirror the Robolectric tests added to <c>ExportImportManagerTest</c> on the
/// device. The two suites are a matched pair: together they pin the contract from both sides, so
/// a tightening on either side shows up as a failing test rather than a rejected export file
/// discovered in the warehouse.
/// </remarks>
public sealed class InventoryValidatorTests
{
    private static Item Item(string name = "Drill", string uuid = "u1") =>
        new() { Name = name, Uuid = uuid };

    private static Container Container(string id = "BOX-101", params Item[] items) =>
        new() { Id = id, Items = items };

    [Fact]
    public void A_valid_document_passes()
    {
        var result = InventoryValidator.ValidateItems([Container("BOX-101", Item())]);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_container_id_is_rejected(string id)
    {
        var result = InventoryValidator.ValidateItems([Container(id, Item())]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing a tag id"));
    }

    [Fact]
    public void A_duplicate_container_id_is_rejected()
    {
        var result = InventoryValidator.ValidateItems(
        [
            Container("BOX-101", Item()),
            Container("BOX-101", Item("Mallet", "u2")),
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate container tag"));
    }

    [Fact]
    public void Container_ids_are_compared_after_trimming()
    {
        // The device trims before testing, so these are the same tag and must be rejected.
        var result = InventoryValidator.ValidateItems(
        [
            Container("BOX-101", Item()),
            Container("  BOX-101  ", Item("Mallet", "u2")),
        ]);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_item_name_is_rejected(string name)
    {
        var result = InventoryValidator.ValidateItems([Container("BOX-101", Item(name))]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing a name"));
    }

    [Fact]
    public void A_container_with_no_items_is_valid()
    {
        var result = InventoryValidator.ValidateItems([Container("EMPTY-1")]);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_zero_or_negative_quantity_is_not_a_contract_violation()
    {
        // The device does not range-check quantity, so rejecting it here would refuse files the
        // handheld accepts. Bad data is a UI concern, not a format concern.
        var result = InventoryValidator.ValidateItems(
        [
            Container("BOX-101",
                new Item { Name = "Odd", Uuid = "u1", Quantity = 0 },
                new Item { Name = "Very odd", Uuid = "u2", Quantity = -5 }),
        ]);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void An_unsupported_version_is_rejected_on_read()
    {
        var json = """{"app":"Scantron","version":"2.0","containers":[]}""";

        var ex = Assert.Throws<InventoryFormatException>(() => InventoryReader.Read(json));
        Assert.Contains("Unsupported export version", ex.Message);
    }

    [Fact]
    public void A_legacy_1_0_document_is_still_readable()
    {
        var json = """
        {"app":"Scantron","version":"1.0","containers":[
          {"id":"BOX-101","items":[{"name":"Drill","quantity":2}]}]}
        """;

        var document = InventoryReader.Read(json);

        Assert.Equal(InventoryFormat.LegacyVersion, document.Version);
        var item = Assert.Single(Assert.Single(document.Containers).Items);
        Assert.Equal("", item.Uuid);
        Assert.Equal(2, item.Quantity);
    }

    [Fact]
    public void A_missing_containers_array_is_rejected()
    {
        var json = """{"app":"Scantron","version":"1.1"}""";

        var ex = Assert.Throws<InventoryFormatException>(() => InventoryReader.Read(json));
        Assert.Contains("containers", ex.Message);
    }

    [Fact]
    public void Malformed_json_is_rejected_as_a_format_error()
    {
        var ex = Assert.Throws<InventoryFormatException>(() => InventoryReader.Read("{not json"));
        Assert.Contains("not valid Scantron JSON", ex.Message);
    }

    [Fact]
    public void A_missing_quantity_defaults_to_one()
    {
        var json = """
        {"version":"1.1","containers":[{"id":"B","items":[{"name":"Drill"}]}]}
        """;

        var item = Assert.Single(Assert.Single(InventoryReader.Read(json).Containers).Items);
        Assert.Equal(1, item.Quantity);
    }

    [Fact]
    public void Container_ids_are_trimmed_on_read()
    {
        var json = """
        {"version":"1.1","containers":[{"id":"  BOX-101  "}]}
        """;

        Assert.Equal("BOX-101", Assert.Single(InventoryReader.Read(json).Containers).Id);
    }

    [Fact]
    public void TryRead_reports_an_error_instead_of_throwing()
    {
        Assert.False(InventoryReader.TryRead("{nope", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryRead_reports_success_for_a_valid_document()
    {
        var ok = InventoryReader.TryRead(
            """{"version":"1.1","containers":[{"id":"B"}]}""",
            out var document,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(document.Containers);
    }
}