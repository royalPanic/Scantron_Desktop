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

/// <summary>Shows an element only when the bound value is <c>null</c>.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility cannot be converted back to a selection");
}
