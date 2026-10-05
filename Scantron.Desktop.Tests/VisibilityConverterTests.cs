using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Scantron.Desktop.ViewModels;
using Scantron.Desktop.Views;
using Xunit;

namespace Scantron.Desktop.Tests;

/// <summary>
/// Pins the visibility predicates the panes bind to.
/// </summary>
/// <remarks>
/// <para>
/// These converters look trivial and are not. <see cref="HasSelectionToVisibleConverter"/> shipped
/// with its sense inverted relative to its name, which collapsed the container editor and the
/// conflict decision panel at exactly the moment they were needed - and nothing failed, because
/// an <c>IValueConverter</c> cannot report that the wrong end of it is being used.
/// </para>
/// <para>
/// The asymmetry is the whole point of each case: an editor is visible while something is selected
/// and hidden when nothing is, and an empty state is the exact inverse. Asserting one direction
/// alone would let the sense flip back.
/// </para>
/// </remarks>
public sealed class VisibilityConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void HasSelection_is_visible_only_while_something_is_selected()
    {
        var converter = new HasSelectionToVisibleConverter();

        Assert.Equal(
            Visibility.Visible,
            converter.Convert(new ContainerViewModel(), typeof(Visibility), null, Culture));

        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(null, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void Empty_collection_shows_the_empty_state()
    {
        var converter = new EmptyToVisibleConverter();

        Assert.Equal(
            Visibility.Visible,
            converter.Convert(new ObservableCollection<string>(), typeof(Visibility), null, Culture));

        Assert.Equal(
            Visibility.Visible,
            converter.Convert(new List<int>(), typeof(Visibility), null, Culture));
    }

    [Fact]
    public void Populated_collection_hides_the_empty_state()
    {
        var converter = new EmptyToVisibleConverter();

        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(new ObservableCollection<string> { "one" }, typeof(Visibility), null, Culture));

        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(new[] { 1, 2 }, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void Unresolved_binding_counts_as_empty()
    {
        // A null result is what an unresolved binding produces, and an operator cannot tell that
        // apart from a genuinely empty list - so it has to show the empty state too.
        var converter = new EmptyToVisibleConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(null, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void No_selection_reads_as_no_container_selected()
    {
        var converter = new ItemPaneEmptyStateConverter();

        Assert.Equal(
            "No container selected",
            converter.Convert([null, Array.Empty<ItemViewModel>()], typeof(string), null, Culture));

        Assert.Equal(
            Visibility.Visible,
            converter.Convert([null, Array.Empty<ItemViewModel>()], typeof(Visibility), null, Culture));
    }

    [Fact]
    public void Selected_but_empty_container_says_so()
    {
        var converter = new ItemPaneEmptyStateConverter();
        var container = new ContainerViewModel();

        Assert.Equal(
            "This container is empty",
            converter.Convert([container, Array.Empty<ItemViewModel>()], typeof(string), null, Culture));
    }

    [Fact]
    public void Body_parameter_selects_the_supporting_line()
    {
        var converter = new ItemPaneEmptyStateConverter();
        var container = new ContainerViewModel();

        Assert.Equal(
            "Pick a container on the left to see the items inside it.",
            converter.Convert([null, Array.Empty<ItemViewModel>()], typeof(string), "Body", Culture));

        Assert.Equal(
            "Use Add item to record the first thing inside it.",
            converter.Convert([container, Array.Empty<ItemViewModel>()], typeof(string), "Body", Culture));
    }

    [Fact]
    public void Rows_present_hides_the_empty_state_entirely()
    {
        var converter = new ItemPaneEmptyStateConverter();
        var values = new object?[]
        {
            new ContainerViewModel(),
            new List<ItemViewModel> { new(new Scantron.Core.Models.Item { Name = "thing" }) },
        };

        Assert.Equal(Visibility.Collapsed, converter.Convert(values, typeof(Visibility), null, Culture));
        Assert.Equal(string.Empty, converter.Convert(values, typeof(string), null, Culture));
    }

    [Fact]
    public void Unresolved_multi_binding_is_handled()
    {
        // WPF passes null here before the first layout pass completes. Throwing would take the
        // window down on startup rather than one frame later.
        var converter = new ItemPaneEmptyStateConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(null, typeof(Visibility), null, Culture));
        Assert.Equal("No container selected", converter.Convert(null, typeof(string), null, Culture));
    }
}
