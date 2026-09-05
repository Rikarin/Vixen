// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The three application-shaped controls the editor had and no application could reach.</summary>
public class ApplicationBarTests {
    static (ControlFixture Fixture, Toolbar Bar) Strip(int buttons = 3) {
        var fixture = new ControlFixture();
        var bar = fixture.Add<Toolbar>();

        for (var i = 0; i < buttons; i++) {
            bar.Add<Button>().Label = "b" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        fixture.Update();

        return (fixture, bar);
    }

    /// <summary>The role that existed with nothing to carry it now has a carrier.</summary>
    [Fact]
    public void A_toolbar_reports_the_toolbar_role_and_a_status_bar_reports_status() {
        using var fixture = new ControlFixture();
        var bar = fixture.Add<Toolbar>();
        var status = fixture.Add<StatusBar>();

        Assert.Equal(AccessibleRole.Toolbar, bar.Role);
        Assert.Equal(AccessibleRole.Status, status.Role);
    }

    /// <summary>
    ///     ⚠ <b>One tab stop for the whole strip.</b> Fifteen buttons that are each a stop puts
    ///     fifteen presses between a keyboard user and the document, which is the reason a toolbar is
    ///     a control rather than a panel with a class.
    /// </summary>
    [Fact]
    public void A_toolbar_is_one_tab_stop_however_many_buttons_it_has() {
        var (fixture, bar) = Strip();

        using (fixture) {
            var items = bar.Items;

            Assert.Equal(3, items.Count);
            Assert.Same(items[0], bar.Active);
            Assert.Equal([0, -1, -1], items.Select(item => item.TabIndex));
        }
    }

    /// <summary>A button added later does not become a second stop.</summary>
    [Fact]
    public void A_button_added_after_the_strip_was_built_joins_the_roving_index() {
        var (fixture, bar) = Strip();

        using (fixture) {
            bar.Add<Button>().Label = "late";
            fixture.Update();

            Assert.Equal([0, -1, -1, -1], bar.Items.Select(item => item.TabIndex));
        }
    }

    /// <summary>The arrows move along the strip and wrap, and Tab is not involved.</summary>
    [Fact]
    public void The_arrows_move_the_stop_along_a_horizontal_strip_and_wrap() {
        var (fixture, bar) = Strip();

        using (fixture) {
            var items = bar.Items;
            fixture.Document.Focus(items[0]);

            fixture.Type(InputKey.Right);
            Assert.Same(items[1], bar.Active);
            Assert.Same(items[1], fixture.Document.Focused);
            Assert.Equal([-1, 0, -1], items.Select(item => item.TabIndex));

            fixture.Type(InputKey.Right);
            fixture.Type(InputKey.Right);
            Assert.Same(items[0], bar.Active);

            fixture.Type(InputKey.Left);
            Assert.Same(items[2], bar.Active);
        }
    }

    /// <summary>A vertical strip answers the other pair, and only the other pair.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that is worth asserting is the refusal.</b> A toolbar that answered all four
    ///     keys would take Left and Right away from whatever they meant in the document behind it,
    ///     which is invisible in the toolbar's own tests and very visible in an application.
    /// </remarks>
    [Fact]
    public void A_vertical_strip_moves_on_up_and_down_and_leaves_left_and_right_alone() {
        var (fixture, bar) = Strip();

        using (fixture) {
            bar.Orientation = Orientation.Vertical;
            fixture.Update();

            var items = bar.Items;
            fixture.Document.Focus(items[0]);

            fixture.Type(InputKey.Right);
            Assert.Same(items[0], bar.Active);

            fixture.Type(InputKey.Down);
            Assert.Same(items[1], bar.Active);
        }
    }

    /// <summary>
    ///     Clicking the third button and tabbing away comes back to the third button.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A roving index maintained only by the arrow keys sends the user back to the
    ///     first.</b> The focus arriving by any route has to move the stop, which is why the strip
    ///     listens for the focus rather than only for keys.
    /// </remarks>
    [Fact]
    public void Focusing_an_item_any_way_at_all_moves_the_stop_to_it() {
        var (fixture, bar) = Strip();

        using (fixture) {
            var items = bar.Items;
            fixture.Document.Focus(items[2]);

            Assert.Same(items[2], bar.Active);
            Assert.Equal([-1, -1, 0], items.Select(item => item.TabIndex));
        }
    }

    /// <summary>A toolbar inside a toolbar keeps its own stop.</summary>
    /// <remarks>
    ///     ⚠ <b>Two strips sharing one roving index fight, and the inner one loses silently</b> — its
    ///     own <c>Rove</c> runs and the outer's overwrites it on the next child.
    /// </remarks>
    [Fact]
    public void A_nested_toolbar_is_not_flattened_into_the_outer_one() {
        var (fixture, bar) = Strip(2);

        using (fixture) {
            var inner = bar.Add<Toolbar>();
            inner.Add<Button>().Label = "inner";
            fixture.Update();

            Assert.Equal(2, bar.Items.Count);
            Assert.Single(inner.Items);
            Assert.Same(inner.Items[0], inner.Active);
        }
    }

    /// <summary>A button inside a group inside the strip is still one of the strip's items.</summary>
    [Fact]
    public void The_strip_finds_focusables_below_a_non_focusable_group() {
        using var fixture = new ControlFixture();
        var bar = fixture.Add<Toolbar>();
        var group = bar.Add<Panel>();
        group.Add<Button>().Label = "in a group";
        fixture.Update();

        Assert.Single(bar.Items);
    }

    /// <summary>The message is the label's text; the cells go in the trailing area after it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Trailing</c> and not <c>Add</c> directly, and the difference is the one every
    ///     control with parts has:</b> <c>UiElement.Add</c> puts a child on the element it was
    ///     called on, and <c>ContentHost</c> is what redirects a <i>nested markup tag</i>. A test
    ///     that called <c>status.Add</c> and expected the trailing area would be asserting on a
    ///     redirection that does not exist in C#.
    /// </remarks>
    [Fact]
    public void A_status_bars_cells_land_after_its_message() {
        using var fixture = new ControlFixture();
        var status = fixture.Add<StatusBar>();

        status.Message = "Ready";
        var cell = status.Trailing.Add<Panel>();
        fixture.Update();

        Assert.Equal("Ready", status.Label.Text);
        Assert.Same(status.Trailing, cell.Parent);
        Assert.Equal([status.Label, status.Trailing], status.Children);
    }

    static (ControlFixture Fixture, SegmentedControl Strip) Segments() {
        var fixture = new ControlFixture();
        var strip = fixture.Add<SegmentedControl>();

        strip.AddSegment("list", "List");
        strip.AddSegment("grid", "Grid");
        strip.AddSegment("gallery", "Gallery");
        fixture.Update();

        return (fixture, strip);
    }

    /// <summary>Exactly one segment is chosen, and it is one question rather than three buttons.</summary>
    [Fact]
    public void A_segmented_control_is_a_radio_group_with_exactly_one_answer() {
        var (fixture, strip) = Segments();

        using (fixture) {
            Assert.Equal(AccessibleRole.RadioGroup, strip.Role);
            Assert.Equal(AccessibleRole.Radio, strip.Segments[0].Role);

            strip.Value = "grid";
            Assert.Equal([false, true, false], strip.Segments.Select(segment => segment.IsChecked));

            strip.Value = "gallery";
            Assert.Equal([false, false, true], strip.Segments.Select(segment => segment.IsChecked));
        }
    }

    /// <summary>Clicking one chooses it and raises once.</summary>
    [Fact]
    public void Clicking_a_segment_chooses_it() {
        var (fixture, strip) = Segments();

        using (fixture) {
            var raised = new List<string?>();
            strip.ValueChanged += (_, value) => raised.Add(value);

            fixture.Click(strip.Segments[1]);

            Assert.Equal("grid", strip.Value);
            Assert.Equal(["grid"], raised);
        }
    }

    /// <summary>Clicking the chosen segment again leaves it chosen.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise the strip reaches a state with nothing selected that the keyboard cannot
    ///     get out of</b> — the same reason a radio cannot be unchecked by clicking it.
    /// </remarks>
    [Fact]
    public void Clicking_the_chosen_segment_again_does_not_unchoose_it() {
        var (fixture, strip) = Segments();

        using (fixture) {
            strip.Value = "grid";
            fixture.Click(strip.Segments[1]);

            Assert.Equal("grid", strip.Value);
            Assert.True(strip.Segments[1].IsChecked);
        }
    }

    /// <summary>The arrows move the choice and wrap, and the roving index follows.</summary>
    [Fact]
    public void The_arrows_move_the_choice_and_wrap() {
        var (fixture, strip) = Segments();

        using (fixture) {
            strip.Value = "list";
            fixture.Document.Focus(strip.Segments[0]);

            fixture.Type(InputKey.Right);
            Assert.Equal("grid", strip.Value);
            Assert.Equal([-1, 0, -1], strip.Segments.Select(segment => segment.TabIndex));

            fixture.Type(InputKey.Right);
            fixture.Type(InputKey.Right);
            Assert.Equal("list", strip.Value);
        }
    }

    /// <summary>A segment written as a nested tag is in the strip, not merely in the tree.</summary>
    /// <remarks>
    ///     ⚠ The state a snapshot cannot reach: the value is set from saved settings before any
    ///     segment exists, so the arriving one has to be given the checked state that implies.
    /// </remarks>
    [Fact]
    public void A_value_set_before_any_segment_exists_is_applied_to_the_one_that_arrives() {
        using var fixture = new ControlFixture();
        var strip = fixture.Add<SegmentedControl>();

        strip.Value = "grid";

        strip.AddSegment("list");
        var grid = strip.AddSegment("grid");
        fixture.Update();

        Assert.True(grid.IsChecked);
        Assert.Equal([-1, 0], strip.Segments.Select(segment => segment.TabIndex));
    }
}
