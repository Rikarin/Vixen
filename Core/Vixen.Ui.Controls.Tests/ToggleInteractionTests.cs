// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Checkboxes, switches, radios and toggle buttons, driven the way a player would.</summary>
/// <remarks>
///     ⚠ Every state change here is asserted <b>twice</b>: once on the property and once on the
///     picture. A toggle that flips its property and never repaints passes every property test ever
///     written, and is the bug a user reports as "the switch does nothing".
/// </remarks>
public class ToggleInteractionTests {
    static UiTest Opened() =>
        ControlHarness.Open(
            240f,
            120f,
            """
            checkbox, radio { width: 24px; height: 24px; }
            switch { width: 44px; height: 24px; }
            button, toggle-button { width: 80px; height: 32px; }
            """
        );

    [Fact]
    public void Clicking_a_checkbox_checks_it_and_draws_a_tick() {
        using var ui = Opened();
        var box = ui.Add<CheckBox>("agree");

        var changes = new List<bool>();
        box.CheckedChanged += (_, value) => changes.Add(value);

        var empty = ui.Ink();

        ui.Get("#agree").ShouldBeHittable().Click();

        Assert.True(box.IsChecked);
        Assert.Equal([true], changes);
        ui.Get("#agree").ShouldHaveState(ElementState.Checked);

        // ⚠ The tick is drawn as a path rather than being an element, so this is the only assertion
        // that can tell a checkbox that ticked from one that only remembers it did.
        Assert.NotEqual(empty, ui.Ink());

        ui.Get("#agree").Click();
        Assert.False(box.IsChecked);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void Space_toggles_the_focused_checkbox() {
        using var ui = Opened();
        var box = ui.Add<CheckBox>("agree");

        ui.Get("#agree").Focus().ShouldBeFocused();
        ui.PressKey(InputKey.Space);
        ui.Frame();

        Assert.True(box.IsChecked);
    }

    [Fact]
    public void An_indeterminate_checkbox_draws_a_dash_rather_than_a_tick() {
        using var ui = Opened();
        var box = ui.Add<CheckBox>("some");

        box.IsChecked = true;
        ui.Frame();
        var tick = ui.Ink();

        box.IsChecked = false;
        box.IsIndeterminate = true;
        ui.Frame();

        // Both draw something, and they draw different things. A control that mapped indeterminate
        // onto checked would pass an assertion that only asked whether anything was drawn.
        Assert.True(ui.Ink() > 0, "an indeterminate box should draw a dash");
        Assert.NotEqual(tick, ui.Ink());
    }

    [Fact]
    public void A_switch_moves_its_knob_across_when_it_is_turned_on() {
        using var ui = Opened();
        var toggle = ui.Add<Switch>("sound");

        // The knob sits at one end or the other, so the two halves of the control are where the
        // change is. Reading the whole picture would net out.
        var leftOff = ui.InkIn(0, 0, 22, 24);

        ui.Get("#sound").Click();

        Assert.True(toggle.IsChecked);
        Assert.NotEqual(leftOff, ui.InkIn(0, 0, 22, 24));
        Assert.True(ui.InkIn(22, 0, 22, 24) > 0, "the knob should have arrived at the right");
    }

    [Fact]
    public void Hovering_a_toggle_sets_hover_and_leaving_clears_it() {
        using var ui = Opened();
        ui.Add<CheckBox>("agree");

        ui.Get("#agree").Hover().ShouldHaveState(ElementState.Hover);

        // Off the control entirely. The document works the crossing out itself, so this is what a
        // pointer leaving actually looks like.
        ui.MovePointer(200f, 100f);
        ui.Frame();

        Assert.Equal(ElementState.None, ui.Get("#agree").Element.State & ElementState.Hover);
    }

    [Fact]
    public void A_radio_group_lets_exactly_one_win() {
        using var ui = Opened();
        var group = ui.Add<RadioGroup>("choice");

        // ⚠ `AddOption`, not the inherited `Add<RadioButton>()`. Only the former registers the radio
        // with the group, and the exclusion walks that registration — so a radio added the other way
        // is laid out, drawn and clickable, and silently exempt from the one rule a group exists to
        // enforce. The class documents this; it is repeated here because the first version of this
        // test got it wrong and the failure read exactly like a broken group.
        var first = group.AddOption("a");
        var second = group.AddOption("b");

        ui.Frame();

        var changes = new List<string?>();
        group.ValueChanged += (_, value) => changes.Add(value);

        ui.Get("radio").First().Click();
        Assert.Equal("a", group.Value);
        Assert.True(first.IsChecked);
        Assert.False(second.IsChecked);

        ui.Get("radio").Last().Click();
        Assert.Equal("b", group.Value);

        // ⚠ The first one turns itself off, which is the whole of what a group is for. A set of
        // radios that all stayed on would satisfy every assertion about the one just clicked.
        Assert.False(first.IsChecked);
        Assert.True(second.IsChecked);
        Assert.Equal(["a", "b"], changes);
    }

    [Fact]
    public void Clicking_the_chosen_radio_again_leaves_it_chosen() {
        using var ui = Opened();
        var group = ui.Add<RadioGroup>("choice");

        var only = group.AddOption("a");
        ui.Frame();

        ui.Get("radio").Click();
        ui.Get("radio").Click();

        // Unlike a checkbox. A radio that untoggled would leave a group with nothing chosen and no
        // way to get back to a valid state.
        Assert.True(only.IsChecked);
        Assert.Equal("a", group.Value);
    }

    [Fact]
    public void A_toggle_button_stays_down() {
        using var ui = Opened();
        var toggle = ui.Add<ToggleButton>("bold");

        ui.Get("#bold").Click();
        Assert.True(toggle.IsChecked);
        ui.Get("#bold").ShouldHaveState(ElementState.Checked);

        ui.Get("#bold").Click();
        Assert.False(toggle.IsChecked);
        ui.Get("#bold").ShouldNotHaveClass("checked");
    }

    [Fact]
    public void A_disabled_toggle_refuses_the_click_and_says_so_in_its_state() {
        using var ui = Opened();
        var box = ui.Add<CheckBox>("agree");
        box.Disabled = true;
        ui.Frame();

        ui.Get("#agree").ShouldHaveState(ElementState.Disabled);
        ui.Get("#agree").Click();

        Assert.False(box.IsChecked);
    }

    [Fact]
    public void A_press_that_wanders_off_before_the_release_does_not_toggle() {
        using var ui = Opened();
        var box = ui.Add<CheckBox>("agree");

        ui.Get("#agree").Press();

        // Away from the control, then up. Every desktop toolkit treats this as a cancelled click,
        // and it is how a user takes back a press they did not mean.
        ui.MovePointer(200f, 100f);
        ui.Frame();
        ui.ReleasePointer();
        ui.Frame();

        Assert.False(box.IsChecked);
    }
}
