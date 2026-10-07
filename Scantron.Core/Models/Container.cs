namespace Scantron.Core.Models;

/// <summary>
/// A physical container, identified by its tag (for example "BOX-101").
/// </summary>
/// <remarks>
/// Mirrors the <c>containers</c> table in the Android app. <see cref="Id"/> is the natural key
/// and is compared on trimmed text, because the handheld trims container ids on import.
/// </remarks>
public sealed record Container
{
    /// <summary>Container tag, e.g. "BOX-101". Required and unique within a document.</summary>
    public required string Id { get; init; }

    /// <summary>Optional friendly label.</summary>
    public string Name { get; init; } = "";

    /// <summary>Physical location, e.g. "Shelf 2-A".</summary>
    public string Location { get; init; } = "";


    /// <summary>Free-form details.</summary>
    public string Notes { get; init; } = "";

    /// <summary>
    /// Epoch milliseconds of the last edit. Stamped independently by each device, so it is a
    /// change <em>signal</em> rather than a trustworthy clock.
    /// </summary>
    public long UpdatedAt { get; init; }

    /// <summary>
    /// Items held in this container. An empty list is legal and means an empty container.
    /// </summary>
    public IReadOnlyList<Item> Items { get; init; } = [];
}
