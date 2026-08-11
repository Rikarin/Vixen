// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Every control, drawn, and compared with a committed picture.</summary>
/// <remarks>
///     <para>
///         What the rest of this project cannot check. A control's state is asserted a hundred ways
///         above; whether the theme's <c>:hover</c> rule actually reaches it, whether its icon is
///         centred, whether a disabled one is dimmer, and whether any of that changed when somebody
///         edited a stylesheet are all questions about pixels.
///     </para>
///     <para>
///         ⚠ <b>These are exact comparisons.</b> Nothing here is rendered by a driver — see
///         <see cref="SoftwareUiRasterizer" /> — so a one-pixel shift is a failure rather than
///         something under a threshold. That is a stronger guarantee than a GPU golden suite can
///         offer and it is also a stricter one: a deliberate change to the theme will turn a lot of
///         these red at once, which is correct, and accepting it is one environment variable.
///     </para>
///     <para>
///         ⚠ <b>Small pictures.</b> A regression suite's cost is the bytes in git, not the
///         rendering, and a control drawn wrongly is as visible at 120 pixels as at 1200. Each
///         fixture is sized to the control it is about rather than to a screen.
///     </para>
///     <para>
///         <b>The states matter more than the controls.</b> A button at rest is drawn by the same
///         four lines as a button hovered; what the picture is for is the second one, because a
///         <c>:hover</c> rule that stopped matching is invisible to every other kind of test.
///     </para>
/// </remarks>
public class ControlVisualTests {
    static UiTest Opened(float width, float height, string? css = null) =>
        ControlHarness.Open(width, height, css);

    [Fact]
    public void Button_at_rest_hovered_pressed_and_disabled() {
        using var ui = Opened(120f, 40f, "button { width: 100px; height: 32px; }");
        var button = ui.Add<Button>("go");
        button.Label = "Go";
        ui.Frame();

        ui.Screenshot("button-rest");

        ui.Get("#go").Hover();
        ui.Screenshot("button-hover");

        ui.Get("#go").Press();
        ui.Screenshot("button-active");

        ui.Get("#go").Release();
        button.Disabled = true;
        ui.Frame();
        ui.Screenshot("button-disabled");
    }

    [Fact]
    public void Button_variants() {
        using var ui = Opened(120f, 40f, "button { width: 100px; height: 32px; }");
        var button = ui.Add<Button>("go");
        button.Label = "Go";

        foreach (var variant in new[] {
                     ControlVariant.Default, ControlVariant.Primary, ControlVariant.Danger
                 }) {
            button.Variant = variant;
            ui.Frame();
            ui.Screenshot($"button-{variant.ToString().ToLowerInvariant()}");
        }
    }

    [Fact]
    public void Checkbox_unchecked_checked_and_indeterminate() {
        using var ui = Opened(40f, 40f, "checkbox { width: 24px; height: 24px; }");
        var box = ui.Add<CheckBox>("agree");
        ui.Frame();

        ui.Screenshot("checkbox-off");

        box.IsChecked = true;
        ui.Frame();
        ui.Screenshot("checkbox-on");

        box.IsChecked = false;
        box.IsIndeterminate = true;
        ui.Frame();
        ui.Screenshot("checkbox-indeterminate");
    }

    [Fact]
    public void Radio_off_and_on() {
        using var ui = Opened(60f, 40f, "radio { width: 24px; height: 24px; }");
        var group = ui.Add<RadioGroup>("choice");
        group.AddOption("a");
        ui.Frame();

        ui.Screenshot("radio-off");

        group.Value = "a";
        ui.Frame();
        ui.Screenshot("radio-on");
    }

    [Fact]
    public void Switch_off_and_on() {
        using var ui = Opened(60f, 40f, "switch { width: 44px; height: 24px; }");
        var toggle = ui.Add<Switch>("sound");
        ui.Frame();

        ui.Screenshot("switch-off");

        toggle.IsChecked = true;
        ui.Frame();
        ui.Screenshot("switch-on");
    }

    [Fact]
    public void Slider_at_each_end_and_in_the_middle() {
        using var ui = Opened(160f, 32f, "slider { width: 140px; height: 24px; }");
        var slider = ui.Add<Slider>("volume");
        ui.Frame();

        ui.Screenshot("slider-min");

        // ⚠ Driven by dragging rather than by assigning, so the picture is of a slider somebody
        // moved. A screenshot taken after setting the property would look identical whether or not
        // dragging worked at all.
        ui.Get("#volume").DragTo(80f, 12f);
        ui.Screenshot("slider-middle");

        ui.Get("#volume").DragTo(200f, 12f);
        ui.Screenshot("slider-max");

        slider.Step = 0.25f;
        ui.Frame();
        ui.Get("#volume").DragTo(60f, 12f);
        ui.Screenshot("slider-stepped");
    }

    [Fact]
    public void Range_slider_with_a_span() {
        using var ui = Opened(160f, 32f, "range-slider { width: 140px; height: 24px; }");
        var range = ui.Add<RangeSlider>("span");

        range.Low = 0.25f;
        range.High = 0.75f;
        ui.Frame();

        ui.Screenshot("range-slider");
    }

    [Fact]
    public void Progress_bar_empty_part_way_and_full() {
        using var ui = Opened(160f, 24f, "progress-bar { width: 140px; height: 8px; }");
        var progress = ui.Add<ProgressBar>("loading");
        ui.Frame();

        ui.Screenshot("progress-empty");

        progress.Value = 0.6f;
        ui.Frame();
        ui.Screenshot("progress-partial");

        progress.Value = 1f;
        ui.Frame();
        ui.Screenshot("progress-full");
    }

    [Fact]
    public void Spinner_at_two_phases() {
        using var ui = Opened(40f, 40f, "spinner { width: 24px; height: 24px; }");
        var spinner = ui.Add<Spinner>("busy");
        ui.Frame();

        ui.Screenshot("spinner-start");

        spinner.Phase = 0.25f;
        ui.Frame();
        ui.Screenshot("spinner-quarter");
    }

    [Fact]
    public void Text_box_empty_and_filled_and_focused() {
        using var ui = Opened(180f, 44f, "text-box { width: 160px; height: 32px; }");
        ui.Add<TextBox>("name");
        ui.Frame();

        ui.Screenshot("textbox-empty");

        ui.Get("#name").Type("Vix");
        ui.Screenshot("textbox-typed");
    }

    [Fact]
    public void Select_closed() {
        using var ui = Opened(160f, 44f, "select { width: 140px; height: 32px; }");
        var select = ui.Add<Select>("pick");

        select.AddOption("one", "One");
        select.AddOption("two", "Two");
        ui.Frame();

        ui.Screenshot("select-closed");
    }

    [Fact]
    public void Tabs_with_one_selected() {
        using var ui = Opened(220f, 44f, "tabs { width: 200px; height: 32px; }");
        var tabs = ui.Add<Tabs>("views");

        tabs.AddTab("One");
        tabs.AddTab("Two");
        ui.Frame();

        ui.Get("tab").Last().Click();
        ui.Screenshot("tabs-second-selected");
    }

    [Fact]
    public void Badge_alert_and_separator() {
        using var ui = Opened(200f, 120f, "badge { width: 60px; height: 20px; } alert { width: 180px; height: 40px; }");

        var badge = ui.Add<Badge>("count");
        badge.Text = "9";

        var alert = ui.Add<Alert>("notice");
        alert.Title = "Careful";

        ui.Add<Separator>("rule");
        ui.Frame();

        ui.Screenshot("display-badge-alert-separator");
    }

    [Fact]
    public void Card_and_panel() {
        using var ui = Opened(200f, 120f, "card { width: 180px; height: 100px; }");
        var card = ui.Add<Card>("card");
        card.Add("div").Text = "Body";
        ui.Frame();

        ui.Screenshot("card");
    }

    [Fact]
    public void Expander_collapsed_and_expanded() {
        using var ui = Opened(220f, 100f, "expander { width: 200px; }");
        var expander = ui.Add<Expander>("details");

        expander.Label = "More";
        expander.Content.Add("div").Text = "Inside";
        ui.Frame();

        ui.Screenshot("expander-collapsed");

        ui.Get("expander-header").Click();
        ui.Screenshot("expander-expanded");
    }

    [Fact]
    public void Scroll_view_with_a_vertical_bar() {
        using var ui = Opened(140f, 100f, "scroll-view { width: 120px; height: 80px; }");
        var scroll = ui.Add<ScrollView>("list");

        for (var i = 0; i < 10; i++) {
            scroll.Content.Add("div").SetStyle("height", "20px");
        }

        ui.Frame();

        // This used to need an explicit `scroll.Refresh()` between the two frames, because the bars
        // synced on a scroll and on nothing else — a view whose content had just been filled showed
        // no scrollbar until something scrolled it. `ScrollView` is on `UiDocument.LayoutFinished`
        // now, so the frame above is enough. The second frame stays: the first is what makes the
        // content's height a measurement rather than a declaration.
        ui.Frame();

        // The picture that would have caught the orientation bug. A vertical scrollbar carrying the
        // horizontal class is laid out along the bottom edge, which no assertion about the scroll
        // offset can see and which is the first thing anybody notices on screen.
        ui.Screenshot("scroll-view-idle");

        ui.Get("#list").Scroll(0f, 60f);
        ui.Screenshot("scroll-view-scrolled");
    }

    [Fact]
    public void Dialog_over_a_page() {
        using var ui = Opened(240f, 160f, "button { width: 80px; height: 28px; } dialog { width: 240px; height: 160px; }");
        ui.Add<Button>("behind").Label = "Behind";

        var dialog = ui.Add<Dialog>("confirm");
        dialog.Title = "Sure?";
        ui.Frame();

        ui.Screenshot("dialog-closed");

        dialog.Open();
        ui.Frame();

        // The backdrop dims what is behind it, which is the whole visual point of a modal and is a
        // pure rendering claim.
        ui.Screenshot("dialog-open");
    }

    [Fact]
    public void Menu_with_items() {
        using var ui = Opened(160f, 120f, "menu { width: 140px; }");
        var menu = ui.Add<Menu>("actions");

        menu.AddItem("Cut");
        menu.AddItem("Copy");
        menu.AddSeparator();
        menu.AddItem("Paste");

        menu.Open();
        ui.Frame();

        ui.Screenshot("menu-open");
    }

    [Fact]
    public void Toast_in_its_host() {
        using var ui = Opened(240f, 100f, "toast { width: 200px; }");
        var host = ui.Add<ToastHost>("toasts");

        host.Show("Saved");
        ui.Frame();

        ui.Screenshot("toast");
    }
}
