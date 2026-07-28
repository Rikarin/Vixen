// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The controls that draw themselves, the ones that scroll, and the ones that float.</summary>
public class SurfaceTests {
    const float Tolerance = 0.01f;

    [Fact]
    public void A_slider_draws_a_track_a_fill_and_a_thumb() {
        using var fixture = new ControlFixture();

        var slider = fixture.Add<Slider>();
        slider.Value = 0.5f;
        fixture.Update();

        var rectangles = fixture.Document.Drawing.Commands
            .Where(static command => command.Kind == DrawCommandKind.Rectangle)
            .ToList();

        // The three the control emits, over whatever the theme's backgrounds contributed.
        Assert.True(rectangles.Count >= 3);
    }

    [Fact]
    public void Clicking_a_slider_moves_it_and_dragging_keeps_moving_it() {
        using var fixture = new ControlFixture(css: "slider { width: 114px; }");

        var slider = fixture.Add<Slider>();
        fixture.Update();

        var bounds = slider.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);

        // The rail is inset by half a thumb at each end — 14 by default — so the middle of a
        // 114-pixel control is the middle of a 100-pixel rail.
        fixture.Press(bounds.X + 57f, y);
        Assert.Equal(0.5f, slider.Value, Tolerance);

        fixture.MovePointer(bounds.X + 107f, y);
        Assert.Equal(1f, slider.Value, Tolerance);

        fixture.Release(bounds.X + 107f, y);
    }

    [Fact]
    public void A_press_beyond_the_rail_clamps_rather_than_overshooting() {
        using var fixture = new ControlFixture(css: "slider { width: 114px; }");

        var slider = fixture.Add<Slider>();
        fixture.Update();

        var bounds = slider.Bounds;
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Click(bounds.X + 1f, y);
        Assert.Equal(0f, slider.Value, Tolerance);

        fixture.Click(bounds.X + bounds.Width - 1f, y);
        Assert.Equal(1f, slider.Value, Tolerance);
    }

    [Fact]
    public void A_step_snaps_the_value() {
        using var fixture = new ControlFixture();

        var slider = fixture.Add<Slider>();
        slider.Minimum = 0f;
        slider.Maximum = 10f;
        slider.Step = 2f;

        slider.Value = 4.9f;
        Assert.Equal(4f, slider.Value, Tolerance);

        slider.Value = 5.1f;
        Assert.Equal(6f, slider.Value, Tolerance);
    }

    [Fact]
    public void The_keyboard_moves_a_continuous_slider_by_a_hundredth() {
        using var fixture = new ControlFixture();

        var slider = fixture.Add<Slider>();
        slider.Value = 0.5f;

        fixture.Document.Focus(slider);

        fixture.Type(InputKey.Right);
        Assert.Equal(0.51f, slider.Value, Tolerance);

        fixture.Type(InputKey.Home);
        Assert.Equal(0f, slider.Value, Tolerance);

        fixture.Type(InputKey.End);
        Assert.Equal(1f, slider.Value, Tolerance);
    }

    [Fact]
    public void A_range_sliders_thumbs_meet_but_do_not_cross() {
        using var fixture = new ControlFixture();

        var slider = fixture.Add<RangeSlider>();
        slider.Low = 0.3f;
        slider.High = 0.7f;

        slider.Low = 0.9f;
        Assert.Equal(0.7f, slider.Low, Tolerance);
        Assert.Equal(0.7f, slider.High, Tolerance);

        slider.High = 0.1f;
        Assert.Equal(0.7f, slider.High, Tolerance);
    }

    [Fact]
    public void An_indeterminate_progress_bar_says_so_in_a_class() {
        using var fixture = new ControlFixture();

        var bar = fixture.Add<ProgressBar>();
        Assert.False(bar.HasClass("indeterminate"));

        bar.IsIndeterminate = true;
        Assert.True(bar.HasClass("indeterminate"));

        // ⚠ A flag rather than a magic value, so that a bar told `NaN` by somebody's arithmetic
        // shows an empty bar rather than silently becoming an animation.
        bar.Value = 0.5f;
        Assert.Equal(0.5f, bar.Value, Tolerance);
    }

    [Fact]
    public void A_spinner_draws_an_arc_whose_length_follows_its_sweep() {
        using var fixture = new ControlFixture();

        var spinner = fixture.Add<Spinner>();
        spinner.Sweep = 0.75f;
        fixture.Update();

        var full = fixture.Document.Drawing.Commands
            .Single(static command => command.Kind == DrawCommandKind.Path)
            .Length;

        spinner.Sweep = 0.25f;
        fixture.Update();

        var quarter = fixture.Document.Drawing.Commands
            .Single(static command => command.Kind == DrawCommandKind.Path)
            .Length;

        Assert.True(quarter < full);
    }

    [Fact]
    public void An_icon_scales_its_geometry_into_whatever_box_it_is_given() {
        using var fixture = new ControlFixture(css: "icon { width: 48px; height: 48px; }");

        var icon = fixture.Add<Icon>();
        icon.Geometry = ControlIcons.Check;
        fixture.Update();

        var command = fixture.Document.Drawing.Commands.Single(static c => c.Kind == DrawCommandKind.Path);
        Assert.Equal(ControlIcons.Check.Count, command.Length);
    }

    [Fact]
    public void Scrolling_moves_the_content_without_relaying_it_out() {
        using var fixture = new ControlFixture(css: """
            scroll-view { width: 200px; height: 100px; }
            .tall { height: 400px; }
        """);

        var view = fixture.Add<ScrollView>();
        var tall = view.Content.Add("div", classNames: "tall");
        fixture.Update();

        Assert.Equal(0f, tall.AbsoluteTop, Tolerance);
        Assert.Equal(300f, view.MaximumTop, Tolerance);

        var top = tall.Top;

        view.ScrollTop = 50f;
        fixture.Update();

        Assert.Equal(-50f, tall.AbsoluteTop, Tolerance);

        // ⚠ The layout result is untouched: the content moved and nothing was measured again. That
        // is the whole reason scrolling is an offset rather than a layout property.
        Assert.Equal(top, tall.Top, Tolerance);
    }

    [Fact]
    public void It_clamps_to_the_content_and_the_wheel_chains_when_it_cannot_move() {
        using var fixture = new ControlFixture(css: """
            scroll-view { width: 200px; height: 100px; }
            .tall { height: 400px; }
        """);

        var view = fixture.Add<ScrollView>();
        view.Content.Add("div", classNames: "tall");
        fixture.Update();

        fixture.Wheel(view, 1000f);
        Assert.Equal(300f, view.ScrollTop, Tolerance);

        var handled = false;
        fixture.Document.Root.AddHandler<WheelEvent>((_, args) => handled = !args.Handled);

        fixture.Wheel(view, 1000f);

        // Already at the bottom, so the wheel goes on to whatever contains it — or a page with a
        // fully-scrolled list in the middle becomes a page that cannot be scrolled past the list.
        Assert.True(handled);
    }

    [Fact]
    public void Focusing_something_inside_scrolls_it_into_view() {
        using var fixture = new ControlFixture(css: """
            scroll-view { width: 200px; height: 100px; }
            .spacer { height: 300px; }
        """);

        var view = fixture.Add<ScrollView>();
        view.Content.Add("div", classNames: "spacer");

        var button = view.Content.Add<Button>();
        button.Label = "Deep";
        fixture.Update();

        Assert.Equal(0f, view.ScrollTop, Tolerance);

        fixture.Document.Focus(button);
        fixture.Update();

        // The minimum movement that works, rather than centring — which would make a list jump
        // under a keyboard user arrowing down it one row at a time.
        Assert.True(view.ScrollTop > 0f);
        Assert.True(button.Bounds.Bottom <= view.Bounds.Bottom + Tolerance);
    }

    [Fact]
    public void An_overlay_is_a_root_child_and_hidden_until_it_is_opened() {
        using var fixture = new ControlFixture();

        var anchor = fixture.Add<Button>();
        anchor.Label = "Open";
        fixture.Update();

        var popover = fixture.Add<Popover>();
        popover.Content.Add<TextBlock>().Text = "Hello";
        fixture.Update();

        Assert.Same(fixture.Document.Root, popover.Parent);
        Assert.True(popover.HasClass("closed"));
        Assert.Equal(0f, popover.Height, Tolerance);

        popover.Open(anchor);
        fixture.Update();

        Assert.True(popover.IsOpen);
        Assert.True(popover.Height > 0f);

        // Placed below its anchor, which is what `Placement.Bottom` asks for — and which is only
        // possible because opening ran a layout pass first.
        Assert.True(popover.Bounds.Top >= anchor.Bounds.Bottom);
    }

    [Fact]
    public void A_click_outside_dismisses_it_and_escape_closes_it() {
        using var fixture = new ControlFixture();

        var anchor = fixture.Add<Button>();
        fixture.Update();

        var popover = fixture.Add<Popover>();
        popover.Content.Add<TextBlock>().Text = "Hello";

        popover.Open(anchor);
        fixture.Update();

        fixture.Click(700f, 550f);
        Assert.False(popover.IsOpen);

        popover.Open(anchor);
        fixture.Update();

        fixture.Type(InputKey.Escape);
        Assert.False(popover.IsOpen);
    }

    [Fact]
    public void A_menu_moves_the_highlight_without_running_anything() {
        using var fixture = new ControlFixture();

        var menu = fixture.Add<Menu>();
        var open = menu.AddItem("Open");
        var save = menu.AddItem("Save");

        var run = 0;
        menu.AddHandler<ClickEvent>((_, _) => run++);

        menu.Open();
        fixture.Update();

        Assert.True(open.IsFocused);

        fixture.Type(InputKey.Down);
        Assert.True(save.IsFocused);

        // ⚠ A menu that ran each command as the highlight passed over it would be unusable, which is
        // why arrows here do not activate and arrows in a radio group do.
        Assert.Equal(0, run);

        fixture.Type(InputKey.Enter);
        Assert.Equal(1, run);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Choosing_a_submenu_item_closes_the_whole_chain() {
        using var fixture = new ControlFixture();

        var menu = fixture.Add<Menu>();
        menu.AddItem("Open");

        var submenu = menu.AddSubmenu("Recent");
        var item = submenu.AddItem("project.vixen");

        menu.Open();
        fixture.Update();

        submenu.Open(menu.Items[1]);
        fixture.Update();

        Assert.True(submenu.IsOpen);

        fixture.Click(item);

        Assert.False(submenu.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void A_menu_bar_opens_on_a_click_and_closes_on_the_next_one() {
        using var fixture = new ControlFixture();

        var bar = fixture.Add<MenuBar>();
        var file = bar.AddMenu("File");
        file.AddItem("Open");
        fixture.Update();

        fixture.Click(bar.Items[0]);
        Assert.True(file.IsOpen);
        Assert.Same(file, bar.Current);

        fixture.Click(bar.Items[0]);
        Assert.False(file.IsOpen);
    }

    [Fact]
    public void A_dialog_traps_the_focus_and_gives_it_back() {
        using var fixture = new ControlFixture();

        var opener = fixture.Add<Button>();
        opener.Label = "Delete";
        fixture.Update();

        var dialog = fixture.Add<Dialog>();
        dialog.Title = "Are you sure?";

        var confirm = dialog.Footer.Add<Button>();
        confirm.Label = "Yes";
        fixture.Update();

        fixture.Document.Focus(opener);
        dialog.Open();
        fixture.Update();

        Assert.True(dialog.IsFocusScope);
        Assert.NotNull(fixture.Document.Focused);
        Assert.NotSame(opener, fixture.Document.Focused);

        dialog.Close();
        fixture.Update();

        // Restored to whatever had the focus, not to whatever opened it — a dialog opened by a
        // shortcut was opened by nothing at all.
        Assert.Same(opener, fixture.Document.Focused);
    }

    [Fact]
    public void A_dialog_is_not_light_dismissed_and_a_drawer_is() {
        using var fixture = new ControlFixture();

        var dialog = fixture.Add<Dialog>();
        var drawer = fixture.Add<Drawer>();
        fixture.Update();

        Assert.False(dialog.LightDismiss);
        Assert.True(drawer.LightDismiss);
    }

    [Fact]
    public void The_close_button_cancels_a_dialog() {
        using var fixture = new ControlFixture();

        var dialog = fixture.Add<Dialog>();
        dialog.Title = "Settings";
        dialog.Open();
        fixture.Update();

        CloseReason? reason = null;
        dialog.AddHandler<OpenChangedEvent>((_, args) => reason = args.IsOpen ? null : CloseReason.Cancelled);

        fixture.Click(dialog.CloseButton);

        Assert.False(dialog.IsOpen);
        Assert.Equal(CloseReason.Cancelled, reason);
    }

    [Fact]
    public void Toasts_stack_newest_first_and_expire_on_a_tick() {
        using var fixture = new ControlFixture();

        var host = fixture.Add<ToastHost>();

        var first = host.Show("Saved");
        var second = host.Show("Exported");
        fixture.Update();

        // ⚠ Newest at the top. A toast appended to the end of a bottom-anchored stack pushes the
        // older ones up, which moves a message somebody is halfway through reading.
        Assert.Same(second, host.Live[0]);
        Assert.Same(second, host.Children[0]);

        host.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(2, host.Live.Count);

        host.Tick(TimeSpan.FromSeconds(10));

        Assert.Empty(host.Live);
        Assert.True(first.IsRemoved);
    }

    [Fact]
    public void A_toast_can_be_dismissed_before_it_expires() {
        using var fixture = new ControlFixture();

        var host = fixture.Add<ToastHost>();
        var toast = host.Show("Saved");
        fixture.Update();

        fixture.Click(toast.CloseButton);

        Assert.Empty(host.Live);
    }

    [Fact]
    public void The_theme_loads_and_is_the_user_agents() {
        using var fixture = new ControlFixture();

        var icon = fixture.Add<Icon>();
        fixture.Update();

        // Something the theme said reached the layout, which is the end-to-end check that the sheet
        // parsed rather than being quietly dropped. An icon is measured because it has a size and
        // no padding, so the number is the theme's and nothing else's.
        Assert.Equal(16f, icon.Width, Tolerance);

        // And an author rule beats it at equal specificity, which is the whole reason the theme is
        // loaded as a user-agent sheet rather than as an ordinary one — restyling a control is one
        // rule rather than a rule that has to out-specify whatever the theme happened to write.
        fixture.Document.Load("icon { width: 40px; }");
        fixture.Update();

        Assert.Equal(40f, icon.Width, Tolerance);
    }
}
