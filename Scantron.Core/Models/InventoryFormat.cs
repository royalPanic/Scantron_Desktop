using System.Globalization;

namespace Scantron.Core.Models;

/// <summary>
/// Format constants and quirks shared by the reader, writer and validator.
/// </summary>
/// <remarks>
/// The Android side is the authority for all of this; every value here mirrors
/// <c>ExportImportManager</c>. Keeping the quirks in one place is what stops the desktop writer
/// from emitting files the handheld will reject.
/// </remarks>
public static class InventoryFormat
{
    /// <summary>Value the device stamps in <c>app</c>. Not validated on import.</summary>
    public const string AppId = "Scantron";

    /// <summary>Version this library writes.</summary>
    public const string CurrentVersion = "1.1";

    /// <summary>Version that predates item identity. Still imported by the device.</summary>
    public const string LegacyVersion = "1.0";

    /// <summary>File name the device uses by default.</summary>
    public const string DefaultFileName = "scantron_inventory.json";

    /// <summary>Timestamp pattern the device writes: a literal Z, but local time.</summary>
    public const string TimestampPattern = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Every version the current device build can import.</summary>
    public static readonly IReadOnlySet<string> ImportableVersions =
        new HashSet<string>(StringComparer.Ordinal) { LegacyVersion, CurrentVersion };

    /// <summary>
    /// Parses an <c>exportedAt</c> value, correcting for the device's time-zone bug.
    /// </summary>
    /// <remarks>
    /// The handheld builds this string with a hard-coded <c>'Z'</c> and the machine's default
    /// time zone, so 22:30 on a UTC-4 device is written as <c>2026-10-04T22:30:00Z</c>. Parsing
    /// it as literal UTC would report a time four hours later than the export actually happened,
    /// so it is read back as unspecified kind (i.e. the local wall-clock time it denotes).
    /// Returns <see langword="null"/> when the value is absent or unparseable, rather than
    /// guessing - a bad timestamp is worth reporting, not papering over.
    /// </remarks>
    public static DateTimeOffset? ParseExportedAt(string? exportedAt)
    {
        if (string.IsNullOrWhiteSpace(exportedAt))
        {
            return null;
        }

        // Tolerate a genuine UTC stamp too: if the text ends in a real offset, honour it.
        if (DateTimeOffset.TryParse(
                exportedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var withOffset))
        {
            return withOffset;
        }

        if (DateTime.TryParseExact(
                exportedAt,
                TimestampPattern,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var wallClock))
        {
            return new DateTimeOffset(wallClock, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>Formats a timestamp the way the device does: local wall clock, literal Z.</summary>
    public static string FormatExportedAt(DateTimeOffset instant) =>
        instant.ToString(TimestampPattern, CultureInfo.InvariantCulture);
}