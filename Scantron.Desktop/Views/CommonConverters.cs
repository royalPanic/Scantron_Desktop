using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scantron.Desktop.Views;

/// <summary>Shows an element only when the bound value is <c>true</c> or non-null.</summary>
/// <remarks>
/// Two conversions in one converter because both uses in this app are the same predicate: hide
/// a badge that has nothing to report, hide an editor when nothing is selected.
/// </remarks>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// Shows an element only while a selection exists - i.e. while the bound value is non-null.
/// </summary>
/// <remarks>
/// Named for what it does rather than for the type it inspects: the awkward part of a
/// null-check converter is that "null" and "hidden" pull in opposite directions, and a name
/// like <c>NullToVisible</c> reads as "show it when null", which is the inverse of what the
/// call sites want. Both editors that use this are visible precisely while something is
/// selected, so the sense here is "has a selection".
/// </remarks>
public sealed class HasSelectionToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility cannot be converted back to a selection");
}

/// <summary>Shows an element only when the bound collection has nothing in it.</summary>
/// <remarks>
/// Drives the empty states - "No containers found", "This container is empty" - that the
/// handheld shows in place of an empty list. A null binding result counts as empty, because a
/// collection that failed to resolve is the same thing to an operator as one with no rows.
/// </remarks>
public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Visible,
            ICollection collection => collection.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            IEnumerable enumerable => !enumerable.GetEnumerator().MoveNext() ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Collapsed,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility cannot be converted back to a collection");
}

    /// <summary>
    /// Picks the right empty-state wording for the items pane, or hides it when there are rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The item pane has two empty states, not one, and the device distinguishes them: nothing is
    /// selected yet versus a container that genuinely holds nothing. Overlaying both would show two
    /// contradictory messages at once, and showing only the first would claim a container is empty
    /// when no container has been picked.
    /// </para>
    /// <para>
    /// Takes the container and its items as two values so the two questions - is one selected, and
    /// does it have anything in it - can be answered together. The <c>parameter</c> picks which half
    /// of the message to return, so the title and the supporting line stay in step without a second
    /// converter that has to repeat the same branching.
    /// </para>
    /// </remarks>
    public sealed class ItemPaneEmptyStateConverter : IMultiValueConverter
    {
        public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
        {
            // WPF hands a null array here when one of the contributing bindings has not resolved yet,
            // which on first layout is normal rather than exceptional.
            var hasContainer = values is { Length: > 0 } && values[0] is not null;
            var hasItems = values is { Length: > 1 } && values[1] is ICollection { Count: > 0 };

            if (hasContainer && hasItems)
            {
                // Rows are showing: the empty state is not part of the picture at all.
                return targetType == typeof(Visibility) ? Visibility.Collapsed : string.Empty;
            }

            var title = hasContainer ? "This container is empty" : "No container selected";
            var body = hasContainer
                ? "Use Add item to record the first thing inside it."
                : "Pick a container on the left to see the items inside it.";

            return targetType == typeof(Visibility)
                ? Visibility.Visible
                : string.Equals(parameter as string, "Body", StringComparison.OrdinalIgnoreCase) ? body : title;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("An empty-state message cannot be converted back to a selection");
    }
