// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Tabs, disclosure, scrolling and text entry, driven with a pointer and a keyboard.</summary>
public class NavigationInteractionTests {
    static UiTest Opened(string? css = null) =>
        ControlHarness.Open(
            400f,
            300f,
            """
            tabs { width: 300px; height: 40px; }
            expander, accordion { width: 300px; }
            scroll-view { width: 200px; height: 100px; }
            text-box { width: 200px; height: 32px; }
            """
            + Environment.NewLine
            + css
        );

    [Fact]
    public void Clicking_a_tab_selects_it_and_leaves_the_others() {
        using var ui = Opened();
        var tabs = ui.Add<Tabs>("views");

        tabs.AddTab("One");
        tabs.AddTab("Two");
        tabs.AddTab("Three");
        ui.Frame();

        var chosen = new List<int>();
        tabs.SelectionChanged += (_, index) => chosen.Add(index);

        ui.Get("tab").ShouldHaveCount(3);
        ui.Get("tab").Nth(2).Click();

        Assert.Equal(2, tabs.SelectedIndex);
        Assert.Equal([2], chosen);

        // Exactly one is checked, which is the claim a tab strip exists to make.
        ui.Get("tab").Nth(2).ShouldHaveState(ElementState.Checked);
        Assert.Equal(1, ui.Get("tab").Elements.Count(tab => (tab.State & ElementState.Checked) != 0));
    }

    [Fact]
    public void The_arrows_move_the_tab_selection() {
        using var ui = Opened();
        var tabs = ui.Add<Tabs>("views");

        tabs.AddTab("One");
        tabs.AddTab("Two");
        ui.Frame();

        ui.Get("tab").First().Click();
        Assert.Equal(0, tabs.SelectedIndex);

        ui.PressKey(InputKey.Right);
        ui.Frame();

        // A tab strip is one tab stop, so the arrows have to select rather than only move the
        // focus — otherwise the keyboard can reach a tab and never choose it.
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [Fact]
    public void Hovering_a_tab_marks_only_that_tab() {
        using var ui = Opened();
        var tabs = ui.Add<Tabs>("views");

        tabs.AddTab("One");
        tabs.AddTab("Two");
        ui.Frame();

        ui.Get("tab").Last().Hover();

        ui.Get("tab").Last().ShouldHaveState(ElementState.Hover);
        Assert.Equal(ElementState.None, ui.Get("tab").First().Element.State & ElementState.Hover);
    }

    [Fact]
    public void Clicking_an_expander_header_opens_it_and_shows_its_body() {
        using var ui = Opened();
        var expander = ui.Add<Expander>("details");

        var body = expander.Content.Add("div");
        body.Text = "Hidden";
        ui.Frame();

        var opened = new List<bool>();
        expander.Expanded += (_, open) => opened.Add(open);

        var collapsed = ui.Ink();
        Assert.False(expander.IsExpanded);

        ui.Get("expander-header").Click();

        Assert.True(expander.IsExpanded);
        Assert.Equal([true], opened);

        // ⚠ The picture as well as the property. A collapsed body is `display: none`, which is a
        // zero rectangle — so an expander that toggled its flag without restyling would look
        // identical and pass every property assertion.
        Assert.NotEqual(collapsed, ui.Ink());

        ui.Get("expander-header").Click();
        Assert.False(expander.IsExpanded);
    }

    /// <summary>
    ///     ⚠ The chevron is the only thing on a collapsed section saying there is anything inside it,
    ///     and it pointed right in both states. This class used to claim the stylesheet rotated it —
    ///     no such rule was ever written, and there is no <c>transform</c> in the style engine to
    ///     write it with.
    /// </summary>
    [Fact]
    public void An_expanders_chevron_turns_down_when_it_opens() {
        using var ui = Opened();

        var expander = ui.Add<Expander>("details");
        expander.Content.Add("div").Text = "Hidden";

        ui.Frame();

        Assert.Same(ControlIcons.ChevronRight, expander.Header.Chevron.Geometry);

        ui.Get("expander-header").Click();
        Assert.Same(ControlIcons.ChevronDown, expander.Header.Chevron.Geometry);

        ui.Get("expander-header").Click();
        Assert.Same(ControlIcons.ChevronRight, expander.Header.Chevron.Geometry);
    }

    [Fact]
    public void An_accordion_closes_the_last_one_when_it_opens_the_next() {
        using var ui = Opened();
        var accordion = ui.Add<Accordion>("faq");

        // ⚠ Two things the first version of this test got wrong. `AddSection` registers the section
        // with the accordion and the inherited `Add<Expander>()` does not, so a section added the
        // other way is exempt from the exclusion — the same shape as `RadioGroup.AddOption`. And
        // `AllowMultiple` defaults to *true*, so "one at a time" is the opted-in behaviour rather
        // than the default.
        accordion.AllowMultiple = false;

        var first = accordion.AddSection("One");
        var second = accordion.AddSection("Two");
        ui.Frame();

        ui.Get("expander-header").First().Click();
        Assert.True(first.IsExpanded);

        ui.Get("expander-header").Last().Click();

        // One at a time unless told otherwise, which is what makes it an accordion rather than a
        // column of expanders.
        Assert.True(second.IsExpanded);
        Assert.False(first.IsExpanded);
    }

    [Fact]
    public void An_accordion_that_allows_several_keeps_them_open() {
        using var ui = Opened();
        var accordion = ui.Add<Accordion>("faq");
        accordion.AllowMultiple = true;

        var first = accordion.AddSection("One");
        var second = accordion.AddSection("Two");
        ui.Frame();

        ui.Get("expander-header").First().Click();
        ui.Get("expander-header").Last().Click();

        Assert.True(first.IsExpanded);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public void A_wheel_over_a_scroll_view_moves_its_content() {
        using var ui = Opened();
        var scroll = ui.Add<ScrollView>("list");

        for (var i = 0; i < 20; i++) {
            var row = scroll.Content.Add("div");
            row.Text = $"Row {i}";
            row.SetStyle("height", "20px");
        }

        ui.Frame();

        var moved = 0;
        scroll.Scrolled += _ => moved++;

        Assert.Equal(0f, scroll.ScrollTop, 0.001f);

        ui.Get("#list").Scroll(0f, 120f);

        Assert.True(scroll.ScrollTop > 0f, $"expected the content to move, got {scroll.ScrollTop}");
        Assert.True(moved > 0);
    }

    [Fact]
    public void A_scroll_view_will_not_scroll_past_its_content() {
        using var ui = Opened();
        var scroll = ui.Add<ScrollView>("list");

        for (var i = 0; i < 20; i++) {
            scroll.Content.Add("div").SetStyle("height", "20px");
        }

        ui.Frame();

        // Far more than there is room for.
        for (var i = 0; i < 30; i++) {
            ui.Get("#list").Scroll(0f, 200f);
        }

        var bottom = scroll.ScrollTop;
        ui.Get("#list").Scroll(0f, 200f);

        // Clamped, not accumulating. A view that kept counting would take as many wheel clicks to
        // come back as it took to run off the end.
        Assert.Equal(bottom, scroll.ScrollTop, 0.001f);
    }

    [Fact]
    public void A_scrollbars_class_follows_its_orientation() {
        using var ui = Opened();
        var bar = ui.Add<ScrollBar>("bar");

        bar.Orientation = Orientation.Vertical;
        ui.Frame();

        // ⚠ <b>This is a regression test for a real bug.</b> The class was added once in OnCreated
        // from the default orientation and never updated, while every theme rule that decides where
        // a scrollbar sits is keyed on it — so a bar told to be vertical was laid out along the
        // bottom edge, ten pixels tall, and drew and hit-tested itself as though it ran down the
        // side. `Separator` had the change callback; this had copied only the AddClass.
        ui.Get("#bar").ShouldHaveClass("vertical").ShouldNotHaveClass("horizontal");

        bar.Orientation = Orientation.Horizontal;
        ui.Frame();

        ui.Get("#bar").ShouldHaveClass("horizontal").ShouldNotHaveClass("vertical");
    }

    [Fact]
    public void A_scroll_views_vertical_bar_is_styled_as_a_vertical_one() {
        using var ui = Opened();
        var scroll = ui.Add<ScrollView>("list");

        for (var i = 0; i < 20; i++) {
            scroll.Content.Add("div").SetStyle("height", "20px");
        }

        ui.Frame();

        // ⚠ Which is how the bug above reached users: `ScrollView` builds both bars and assigns
        // their orientation *after* construction, so every vertical scrollbar in the set carried
        // the horizontal class. A test on ScrollBar alone would have been just as red, but this is
        // the one that says the defect was reachable without anybody doing anything unusual.
        Assert.True(scroll.VerticalBar.HasClass("vertical"), "the vertical bar should be styled vertical");
        Assert.False(scroll.VerticalBar.HasClass("horizontal"), "and not also horizontal");

        Assert.True(scroll.HorizontalBar.HasClass("horizontal"));
        Assert.False(scroll.HorizontalBar.HasClass("vertical"));

        // And it ends up down the right-hand side rather than along the bottom, which is what the
        // class is for.
        var bounds = scroll.VerticalBar.Bounds;
        Assert.True(bounds.Height > bounds.Width, $"expected a tall bar, got {bounds.Width}×{bounds.Height}");
    }

    [Fact]
    public void Dragging_a_scrollbar_thumb_moves_the_value() {
        using var ui = ControlHarness.Open(200f, 200f);
        var bar = ui.Add<ScrollBar>("bar");

        bar.Orientation = Orientation.Vertical;
        bar.ViewportSize = 100f;
        bar.ContentSize = 400f;
        ui.Frame();

        var scrolled = new List<float>();
        bar.Scrolled += (_, value) => scrolled.Add(value);

        // ⚠ From the bar's own rectangle rather than from coordinates written into the test. The
        // theme positions a vertical scrollbar absolutely against the right edge, so a hard-coded x
        // of a few pixels drags empty space — and the first version of this test did exactly that
        // and reported the scrollbar as broken.
        var bounds = bar.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);

        // A scrollbar's thumb is drawn rather than laid out, so the drag has to be given
        // coordinates. Starting at the element's centre would grab whatever happens to be there.
        ui.Drag(x, bounds.Y + 10f, x, bounds.Y + (bounds.Height * 0.8f));

        Assert.True(bar.Value > 0f, $"expected the bar to move, got {bar.Value}");
        Assert.NotEmpty(scrolled);
    }

    [Fact]
    public void Typing_into_a_text_box_puts_the_characters_in_it() {
        using var ui = Opened();
        var box = ui.Add<TextBox>("name");

        ui.Get("#name").Type("Vixen").ShouldBeFocused();

        Assert.Equal("Vixen", box.Value);
    }

    [Fact]
    public void Backspace_takes_the_last_character_off() {
        using var ui = Opened();
        var box = ui.Add<TextBox>("name");

        ui.Get("#name").Type("Vixen");
        ui.PressKey(InputKey.Backspace);
        ui.Frame();

        Assert.Equal("Vixe", box.Value);
    }

    [Fact]
    public void A_disabled_text_box_takes_no_text() {
        using var ui = Opened();
        var box = ui.Add<TextBox>("name");
        box.Disabled = true;
        ui.Frame();

        ui.Document.Focus(box);
        ui.TypeText("nope");
        ui.Frame();

        Assert.True(string.IsNullOrEmpty(box.Value), $"expected nothing typed, got \"{box.Value}\"");
    }

    [Fact]
    public void Pagination_moves_the_page_when_a_number_is_clicked() {
        using var ui = Opened("pagination { width: 300px; height: 32px; }");
        var pages = ui.Add<Pagination>("pages");

        pages.PageCount = 5;
        ui.Frame();

        var chosen = new List<int>();
        pages.PageChanged += (_, page) => chosen.Add(page);

        ui.Get("page-button").Nth(2).Click();

        Assert.NotEmpty(chosen);
        Assert.Equal(chosen[^1], pages.CurrentPage);
    }
}
