// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>A panel scrolls its own content, and the ones that fill their box do not.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is also an assertion that no box was inserted.</b> The reason
///     <see cref="DockPanel" /> scrolls itself rather than holding a <c>ScrollView</c> is that an
///     interposed box would become the containing block for every percentage length in every panel —
///     so the tests check both halves: that it scrolls, and that the children the builder added are
///     still the panel's own children, laid out against the panel.
/// </remarks>
public class DockPanelScrollTests {
    /// <summary>Fills a panel with rows taller than it is.</summary>
    static UiElement Tall(DockPanel panel, int rows = 40) {
        UiElement? last = null;

        for (var i = 0; i < rows; i++) {
            last = panel.Add("row");
            last.SetStyle("height", "40px");
        }

        return last!;
    }

    [Fact]
    public void A_panel_scrolls_when_there_is_more_content_than_fits() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        Tall(panel);
        fixture.Update();

        Assert.True(panel.Scrolls);
        Assert.True(panel.Overflows);
        Assert.True(panel.MaximumScroll > 0f);

        panel.ScrollTo(120f);
        fixture.Update();

        Assert.Equal(120f, panel.ScrollTop, 0.5f);

        // The offset is on the content itself, which is what "no wrapper" means: the child moved and
        // nothing was reparented to move it.
        Assert.Equal(-120f, panel.Children[0].OffsetY, 0.5f);
        Assert.Same(panel, panel.Children[0].Parent);
    }

    [Fact]
    public void A_panel_that_fits_grows_no_scrollbar_at_all() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        var row = panel.Add("row");
        row.SetStyle("height", "40px");

        fixture.Update();

        Assert.False(panel.Overflows);
        Assert.Null(panel.Bar);

        // ⚠ The claim that makes every existing builder safe: a panel's children are what the builder
        // put in it and nothing else.
        Assert.Same(row, Assert.Single(panel.Children));
    }

    [Fact]
    public void A_panel_that_fills_its_box_does_not_scroll() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("scene", "Scene");

        var canvas = panel.Add("canvas");
        Assert.True(DockPanel.Fills(canvas));

        Tall(panel);
        fixture.Update();

        Assert.False(panel.Scrolls);
        Assert.False(panel.HasClass("scrolls"));

        panel.ScrollTo(120f);
        fixture.Update();

        Assert.Equal(0f, panel.ScrollTop);
        Assert.Equal(0f, panel.Children[0].OffsetY);
    }

    [Fact]
    public void The_wheel_scrolls_a_panel_and_stops_at_the_end() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        Tall(panel);
        fixture.Update();

        fixture.Wheel(panel, 90f);
        Assert.Equal(90f, panel.ScrollTop, 0.5f);

        fixture.Wheel(panel, 100_000f);
        Assert.Equal(panel.MaximumScroll, panel.ScrollTop, 0.5f);

        fixture.Wheel(panel, -100_000f);
        Assert.Equal(0f, panel.ScrollTop, 0.5f);
    }

    [Fact]
    public void A_bar_appears_only_once_the_content_has_overflowed() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        Tall(panel);
        fixture.Update();

        var bar = Assert.IsType<ScrollBar>(panel.Bar);
        Assert.False(bar.HasClass("hidden"));
        Assert.Equal(Orientation.Vertical, bar.Orientation);

        // ⚠ Kept rather than destroyed when the content shrinks back. A bar created and removed on a
        // layout pass would restructure the tree under whoever was dragging the thumb.
        while (panel.Children.Count > 1) {
            panel.Children[0].Remove();
        }

        fixture.Update();

        Assert.False(panel.Overflows);
        Assert.Same(bar, panel.Bar);
        Assert.True(bar.HasClass("hidden"));
    }

    [Fact]
    public void An_offset_panel_settles_rather_than_chasing_its_own_content() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        // ⚠ Content that sizes itself against the panel, which is the shape a wrapper would turn into
        // a loop: with a `ScrollView` in between, the child measures the box whose height is the
        // child. Here there is no such box, so it settles on the first pass and stays.
        var filler = panel.Add("filler");
        filler.SetStyle("height", "100%");

        fixture.Update();

        var height = filler.Height;

        fixture.Update();
        fixture.Update();

        Assert.Equal(height, filler.Height, 0.5f);
        Assert.False(panel.Overflows);
        Assert.Equal(0f, panel.ScrollTop);
    }

    [Fact]
    public void A_panel_scrolled_past_its_end_is_brought_back_when_the_content_shrinks() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        Tall(panel);
        fixture.Update();

        panel.ScrollTo(panel.MaximumScroll);
        fixture.Update();

        Assert.True(panel.ScrollTop > 0f);

        while (panel.Children.Count > 1) {
            panel.Children[0].Remove();
        }

        fixture.Update();

        Assert.Equal(0f, panel.ScrollTop);
        Assert.Equal(0f, panel.Children[0].OffsetY);
    }
}
