using Scantron.Core.Models;

namespace Scantron.Core.Serialization;

/// <summary>
/// Outcome of validating a document, mirroring the Android importer's rejection rules.
/// </summary>
public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success { get; } = new([]);

    public static ValidationResult Failure(string error) => new([error]);
}

/// <summary>
/// Rejects documents the Android app would refuse to import.
/// </summary>
/// <remarks>
/// <para>
/// The device parses and validates a whole document before it touches the database, then clears
/// and replaces. A file that fails here leaves device data intact; a file that passes a partial
/// check may still be rejected on the device. Running this before writing is what keeps the
/// desktop app from producing an unusable export.
/// </para>
/// <para>
/// Each rule below is a deliberate mirror of <c>ExportImportManager.parseImportDocument</c>, and
/// the Robolectric tests for that class are the executable specification. If you change one,
/// change both.
/// </para>
/// </remarks>
public static class InventoryValidator
{
    /// <summary>Validates an in-memory document using the same rules the device applies.</summary>
    public static ValidationResult Validate(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ValidateItems(document.Containers);
    }

    /// <summary>
    /// Validates container and item invariants.
    /// </summary>
    /// <remarks>
    /// The device does not enforce <c>updatedAt</c> or <c>quantity</c> bounds, and neither does
    /// this, because inventing stricter rules here would reject documents the device accepts.
    /// Negative and zero quantities pass through unchanged; that is a data-quality question for
    /// the UI, not a contract violation.
    /// </remarks>
    public static ValidationResult ValidateItems(IReadOnlyList<Container> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < containers.Count; i++)
        {
            var container = containers[i];

            // The device trims the id before its emptiness test, so " " is as invalid as "".
            var containerId = container.Id?.Trim() ?? "";
            if (containerId.Length == 0)
            {
                errors.Add($"Container entry {i} is missing a tag id");
                continue;
            }

            if (!seenIds.Add(containerId))
            {
                errors.Add($"Duplicate container tag \"{containerId}\" in file");
                continue;
            }

            // A missing or empty items array is legal: it just means an empty container.
            foreach (var item in container.Items)
            {
                if (item is null)
                {
                    errors.Add($"Item entry in \"{containerId}\" is not a valid object");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    errors.Add($"An item in \"{containerId}\" is missing a name");
                }
            }
        }

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors);
    }
}