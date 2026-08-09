// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Radio groups, tabs, disclosure and the select family.</summary>
public class SelectionTests {
    [Fact]
    public void A_radio_group_chooses_exactly_one() {
        using var fixture = new ControlFixture();

        var group = fixture.Add<RadioGroup>();
        var red = group.AddOption("red");
        var green = group.AddOption("green");
        fixture.Update();

        fixture.Click(red);
        Assert.True(red.IsChecked);
        Assert.False(green.IsChecked);
        Assert.Equal("red", group.Value);

        fixture.Click(green);
        Assert.False(red.IsChecked);
        Assert.True(green.IsChecked);
        Assert.Equal("green", group.Value);
    }

    [Fact]
    public void A_chosen_radio_cannot_be_unchosen_by_clicking_it_again() {
        using var fixture = new ControlFixture();

        var group = fixture.Add<RadioGroup>();
        var red = group.AddOption("red");
        fixture.Update();

        fixture.Click(red);
        fixture.Click(red);

        // Otherwise a group can be put into a state — nothing chosen — that the user cannot get back
        // out of with the keyboard.
        Assert.True(red.IsChecked);
        Assert.Equal("red", group.Value);
    }

    [Fact]
    public void Arrows_move_the_choice_and_wrap() {
        using var fixture = new ControlFixture();

        var group = fixture.Add<RadioGroup>();
        group.AddOption("a");
        group.AddOption("b");
        group.AddOption("c");
        fixture.Update();

        group.Value = "a";
        fixture.Document.Focus(group.Options[0]);

        fixture.Type(InputKey.Down);
        Assert.Equal("b", group.Value);

        fixture.Type(InputKey.Down);
        fixture.Type(InputKey.Down);

        // A cycle rather than a layout: stopping at the end would make Down at the bottom of a
        // three-item group do nothing at all, which reads as a broken keyboard.
        Assert.Equal("a", group.Value);
    }

    [Fact]
    public void Only_the_chosen_radio_is_a_tab_stop() {
        using var fixture = new ControlFixture();

        var group = fixture.Add<RadioGroup>();
        group.AddOption("a");
        group.AddOption("b");
        group.AddOption("c");

        group.Value = "b";
        fixture.Update();

        Assert.Equal(-1, group.Options[0].TabIndex);
        Assert.Equal(0, group.Options[1].TabIndex);
        Assert.Equal(-1, group.Options[2].TabIndex);
    }

    [Fact]
    public void The_first_tab_selects_itself_and_shows_its_panel() {
        using var fixture = new ControlFixture();

        var tabs = fixture.Add<Tabs>();
        var first = tabs.AddTab("General");
        var second = tabs.AddTab("Advanced");
        fixture.Update();

        Assert.Equal(0, tabs.SelectedIndex);
        Assert.True(first.IsSelected);
        Assert.True(first.Panel.HasClass("selected"));
        Assert.False(second.Panel.HasClass("selected"));

        fixture.Click(second);

        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(second.Panel.HasClass("selected"));
        Assert.False(first.Panel.HasClass("selected"));
    }

    /// <summary>
    ///     ⚠ <b>A tab set written as tags, which is what <c>AddTab</c> could not be.</b> A tab and
    ///     its panel live in different halves of the tree, so before this a <c>&lt;TabItem /&gt;</c>
    ///     landed directly under <c>tabs</c> — unstyled, outside the strip — with a null
    ///     <c>Panel</c> and nothing able to give it one. The pairing is now
    ///     <c>TabItem.OnCreated</c>'s, so the tag and the method reach the same state by the same
    ///     code.
    /// </summary>
    [Fact]
    public void Tabs_can_be_written_as_markup_and_the_panels_are_where_the_content_went() {
        using var fixture = new ControlFixture();

        var sheet = new TabbedSheet { Pages = ["Advanced", "About"] };

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var tabs = Assert.IsType<Tabs>(sheet.Root.Children[0]);

        // The class reached the control's own element, beside the parts it gave itself.
        Assert.True(tabs.HasClass("document-tabs"));

        Assert.Equal(3, tabs.Items.Count);
        Assert.Equal(["General", "Advanced", "About"], tabs.Items.Select(tab => tab.Label));

        // Every tag went into the strip, and every panel into the panels — which is the thing one
        // `ContentHost` cannot say and two can.
        Assert.All(tabs.Items, tab => Assert.Same(tabs.Strip, tab.Parent));
        Assert.All(tabs.Items, tab => Assert.Same(tabs.Panels, tab.Panel.Parent));

        // Content written between a tab's tags is in its panel, not beside its label.
        var slider = Assert.IsType<Slider>(Assert.Single(tabs.Items[0].Panel.Children));
        Assert.Equal(0.25f, slider.Value, 0.001f);

        // The tab's own children are its label part and nothing the markup wrote — which is the
        // failure this guards: before `ContentHost`, the slider was one of them.
        Assert.DoesNotContain(tabs.Items[0].Children, child => child is Slider);

        // And the first one still selects itself, because adding the element is adding the tab.
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.True(tabs.Items[0].Panel.HasClass("selected"));

        fixture.Click(tabs.Items[2]);
        Assert.Equal(2, tabs.SelectedIndex);
    }

    /// <summary>
    ///     ⚠ <b>A tab that leaves takes its panel and its place with it.</b> Markup removes elements
    ///     without calling <c>RemoveTab</c>, so a <c>Tabs</c> that only unregistered from there would
    ///     keep a dead tab in <c>Items</c>, an orphaned <c>tab-panel</c> in the tree, and possibly a
    ///     <c>SelectedIndex</c> pointing at the gap.
    /// </summary>
    [Fact]
    public void A_tab_removed_by_markup_leaves_the_list_and_takes_its_panel() {
        using var fixture = new ControlFixture();

        var sheet = new TabbedSheet { Pages = ["Advanced", "About"] };

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var tabs = Assert.IsType<Tabs>(sheet.Root.Children[0]);
        var leaving = tabs.Items[2];

        fixture.Click(leaving);
        Assert.Equal(2, tabs.SelectedIndex);

        leaving.Remove();
        fixture.Update();

        Assert.Equal(2, tabs.Items.Count);
        Assert.DoesNotContain(leaving, tabs.Items);
        Assert.Equal(2, tabs.Panels.Children.Count);
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [Fact]
    public void An_unselected_panel_takes_no_room_and_keeps_its_content() {
        using var fixture = new ControlFixture();

        var tabs = fixture.Add<Tabs>();
        var first = tabs.AddTab("General");
        var second = tabs.AddTab("Advanced");

        var kept = second.Panel.Add<TextBox>();
        kept.Value = "typed";
        fixture.Update();

        Assert.Equal(0f, second.Panel.Height);

        fixture.Click(second);

        // The whole reason for `display: none` rather than removal: re-selecting a tab costs a
        // restyle rather than a rebuild, and what was in it is still there.
        Assert.True(second.Panel.Height > 0f);
        Assert.Equal("typed", kept.Value);
        Assert.Equal(0f, first.Panel.Height);
    }

    [Fact]
    public void Arrows_move_between_tabs_and_home_and_end_reach_the_ends() {
        using var fixture = new ControlFixture();

        var tabs = fixture.Add<Tabs>();
        tabs.AddTab("a");
        tabs.AddTab("b");
        tabs.AddTab("c");
        fixture.Update();

        fixture.Document.Focus(tabs.Items[0]);

        fixture.Type(InputKey.Right);
        Assert.Equal(1, tabs.SelectedIndex);

        fixture.Type(InputKey.End);
        Assert.Equal(2, tabs.SelectedIndex);

        fixture.Type(InputKey.Home);
        Assert.Equal(0, tabs.SelectedIndex);

        fixture.Type(InputKey.Left);
        Assert.Equal(2, tabs.SelectedIndex);
    }

    [Fact]
    public void Removing_the_selected_tab_selects_a_neighbour() {
        using var fixture = new ControlFixture();

        var tabs = fixture.Add<Tabs>();
        tabs.AddTab("a");
        var second = tabs.AddTab("b");
        tabs.AddTab("c");

        tabs.SelectedIndex = 1;
        fixture.Update();

        Assert.True(tabs.RemoveTab(second));
        fixture.Update();

        Assert.Equal(2, tabs.Items.Count);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(tabs.Items[1].Panel.HasClass("selected"));
    }

    [Fact]
    public void An_expander_shows_its_content_and_a_button_inside_it_does_not_shut_it() {
        using var fixture = new ControlFixture();

        var expander = fixture.Add<Expander>();
        expander.Label = "Transform";

        var inside = expander.Content.Add<Button>();
        inside.Label = "Reset";
        fixture.Update();

        Assert.Equal(0f, expander.Content.Height);

        fixture.Click(expander.Header);
        Assert.True(expander.IsExpanded);
        Assert.True(expander.HasClass("open"));
        Assert.True(expander.Content.Height > 0f);

        fixture.Click(inside);

        // ⚠ A click from inside bubbles through the expander on its way out. One that toggled on any
        // click would shut itself every time somebody used what is inside it.
        Assert.True(expander.IsExpanded);
    }

    [Fact]
    public void An_exclusive_accordion_closes_what_was_open() {
        using var fixture = new ControlFixture();

        var accordion = fixture.Add<Accordion>();
        accordion.AllowMultiple = false;

        var first = accordion.AddSection("One");
        var second = accordion.AddSection("Two");
        fixture.Update();

        first.IsExpanded = true;
        second.IsExpanded = true;

        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public void An_accordion_keeps_both_open_by_default() {
        using var fixture = new ControlFixture();

        var accordion = fixture.Add<Accordion>();
        var first = accordion.AddSection("One");
        var second = accordion.AddSection("Two");

        first.IsExpanded = true;
        second.IsExpanded = true;

        // The single most complained-about pattern in interface design is the default that loses
        // the user's place, so it is not the default.
        Assert.True(first.IsExpanded);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public void A_select_shows_the_chosen_label_and_the_placeholder_when_empty() {
        using var fixture = new ControlFixture();

        var select = fixture.Add<Select>();
        select.Placeholder = "Choose…";
        select.AddOption("mesh", "Mesh");
        select.AddOption("sprite", "Sprite");
        fixture.Update();

        Assert.Equal("Choose…", select.Field.Text);
        Assert.True(select.HasClass("empty"));

        select.Value = "sprite";
        fixture.Update();

        Assert.Equal("Sprite", select.Field.Text);
        Assert.False(select.HasClass("empty"));
        Assert.True(select.Options[1].IsSelected);
    }

    [Fact]
    public void Clicking_a_select_opens_the_list_and_choosing_closes_it() {
        using var fixture = new ControlFixture();

        var select = fixture.Add<Select>();
        select.AddOption("mesh", "Mesh");
        select.AddOption("sprite", "Sprite");
        fixture.Update();

        fixture.Click(select);
        Assert.True(select.IsOpen);

        fixture.Click(select.Options[1]);

        Assert.False(select.IsOpen);
        Assert.Equal("sprite", select.Value);
    }

    [Fact]
    public void A_multi_select_stays_open_and_counts_what_is_chosen() {
        using var fixture = new ControlFixture();

        var select = fixture.Add<MultiSelect>();
        select.Placeholder = "None";
        select.AddOption("a", "Alpha");
        select.AddOption("b", "Beta");
        fixture.Update();

        Assert.Equal("None", select.Field.Text);

        fixture.Click(select);
        fixture.Click(select.Options[0]);

        Assert.True(select.IsOpen);
        Assert.Equal("Alpha", select.Field.Text);

        fixture.Click(select.Options[1]);

        Assert.Equal(2, select.Values.Count);
        Assert.Equal("2 selected", select.Field.Text);

        fixture.Click(select.Options[0]);
        Assert.Equal("Beta", select.Field.Text);
    }

    [Fact]
    public void A_select_opens_from_the_keyboard_and_escapes_shut() {
        using var fixture = new ControlFixture();

        var select = fixture.Add<Select>();
        select.AddOption("a");
        select.AddOption("b");
        fixture.Update();

        fixture.Document.Focus(select);

        fixture.Type(InputKey.Down);
        Assert.True(select.IsOpen);
        Assert.True(select.Options[0].IsFocused);

        fixture.Type(InputKey.Down);
        Assert.True(select.Options[1].IsFocused);

        fixture.Type(InputKey.Escape);
        Assert.False(select.IsOpen);
    }

    [Fact]
    public void A_combo_box_keeps_what_was_typed_even_if_it_is_not_an_option() {
        using var fixture = new ControlFixture();

        var combo = fixture.Add<ComboBox>();
        combo.AddOption("mesh");
        fixture.Update();

        fixture.Document.Focus(combo.Editor);
        fixture.TypeText("something else");

        // The whole difference from a select: the value need not be one of the options.
        Assert.Equal("something else", combo.Value);
    }

    [Fact]
    public void Picking_a_suggestion_fills_the_field_and_puts_the_caret_at_the_end() {
        using var fixture = new ControlFixture();

        var combo = fixture.Add<ComboBox>();
        combo.AddOption("mesh");
        fixture.Update();

        fixture.Click(combo.Toggle);
        Assert.True(combo.List.IsOpen);

        fixture.Click(combo.Options[0]);

        Assert.Equal("mesh", combo.Value);
        Assert.False(combo.List.IsOpen);
        Assert.Equal(4, combo.Editor.CaretIndex);
        Assert.False(combo.Editor.HasSelection);
    }

    [Fact]
    public void A_breadcrumb_marks_its_last_step_and_puts_a_separator_between() {
        using var fixture = new ControlFixture();

        var breadcrumb = fixture.Add<Breadcrumb>();
        breadcrumb.AddStep("Assets");
        breadcrumb.AddStep("Models");
        fixture.Update();

        Assert.Equal(2, breadcrumb.Steps.Count);
        Assert.Equal(3, breadcrumb.Children.Count);
        Assert.Equal(ElementState.None, breadcrumb.Steps[0].State & ElementState.Checked);
        Assert.True((breadcrumb.Steps[1].State & ElementState.Checked) != 0);
    }

    [Fact]
    public void Pagination_pins_the_ends_and_gaps_the_middle() {
        using var fixture = new ControlFixture();

        var pagination = fixture.Add<Pagination>();
        pagination.PageCount = 90;
        pagination.CurrentPage = 44;
        fixture.Update();

        var labels = pagination.Children
            .OfType<PageButton>()
            .Where(static button => !button.HasClass("page-arrow"))
            .Select(static button => button.Label)
            .ToList();

        Assert.Equal(["1", "…", "44", "45", "46", "…", "90"], labels);
    }

    [Fact]
    public void Pagination_is_the_same_width_on_every_page() {
        using var fixture = new ControlFixture();

        var pagination = fixture.Add<Pagination>();
        pagination.PageCount = 12;
        fixture.Update();

        var rows = new List<string[]>();

        for (var page = 0; page < 12; page++) {
            pagination.CurrentPage = page;
            fixture.Update();

            rows.Add([.. Numbers(pagination)]);
        }

        // ⚠ Seven on every page, and it was four at the ends. A row that grows from four buttons to
        // seven and back moves every number under the pointer on every click, so paging with the
        // mouse means re-finding the button each time — the failure the arrows already avoid by
        // staying disabled rather than disappearing.
        Assert.All(rows, row => Assert.Equal(7, row.Length));

        // ⚠ And a gap never stands for a single page: `1 … 3 4 5 … 12` spends a slot as wide as the
        // number it replaced to conceal page 2.
        foreach (var row in rows) {
            for (var i = 1; i < row.Length - 1; i++) {
                if (row[i] != "…") {
                    continue;
                }

                var before = int.Parse(row[i - 1], CultureInfo.InvariantCulture);
                var after = int.Parse(row[i + 1], CultureInfo.InvariantCulture);

                Assert.True(after - before > 2, $"a gap hiding one page: {string.Join(" ", row)}");
            }
        }

        Assert.Equal(["1", "2", "3", "4", "5", "…", "12"], rows[0]);
        Assert.Equal(["1", "…", "4", "5", "6", "…", "12"], rows[4]);
        Assert.Equal(["1", "…", "8", "9", "10", "11", "12"], rows[11]);
    }

    [Fact]
    public void Pagination_shows_every_page_when_they_all_fit() {
        using var fixture = new ControlFixture();

        var pagination = fixture.Add<Pagination>();
        pagination.PageCount = 5;
        pagination.CurrentPage = 2;
        fixture.Update();

        // Five pages into seven slots: an ellipsis here would hide a page for nothing.
        Assert.Equal(["1", "2", "3", "4", "5"], Numbers(pagination));
    }

    [Fact]
    public void A_narrow_window_still_never_gaps_a_single_page() {
        using var fixture = new ControlFixture();

        var pagination = fixture.Add<Pagination>();
        pagination.PageCount = 12;
        pagination.Window = 0;
        pagination.CurrentPage = 2;
        fixture.Update();

        // The boundary between "near the start" and "in the middle" is Window + 2 rather than
        // 2 × Window + 1; the two agree at the default window of one and disagree here.
        Assert.Equal(["1", "2", "3", "…", "12"], Numbers(pagination));
    }

    static List<string> Numbers(Pagination pagination) =>
        [
            .. pagination.Children
                .OfType<PageButton>()
                .Where(static button => !button.HasClass("page-arrow"))
                .Select(static button => button.Label ?? string.Empty)
        ];

    [Fact]
    public void The_arrows_at_the_ends_are_disabled_rather_than_absent() {
        using var fixture = new ControlFixture();

        var pagination = fixture.Add<Pagination>();
        pagination.PageCount = 3;
        fixture.Update();

        var arrows = pagination.Children
            .OfType<PageButton>()
            .Where(static button => button.HasClass("page-arrow"))
            .ToList();

        Assert.Equal(2, arrows.Count);
        Assert.True(arrows[0].Disabled);
        Assert.False(arrows[1].Disabled);

        // A row whose buttons move sideways when you reach the first page is a row where the next
        // click lands on something else.
        pagination.CurrentPage = 2;
        fixture.Update();

        arrows = pagination.Children.OfType<PageButton>().Where(static button => button.HasClass("page-arrow")).ToList();
        Assert.False(arrows[0].Disabled);
        Assert.True(arrows[1].Disabled);
    }
}
