// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a disabled control looks like, which turned out to be "hovered".</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Regression tests for a defect the picture found and no state assertion could.</b>
///         Every <c>:hover</c> and <c>:active</c> rule in the theme was written without a
///         <c>:not(:disabled)</c> guard, so a disabled control under the pointer lit up exactly as
///         an enabled one does — and then did nothing when it was clicked. The state was right the
///         whole time: the element had <c>Disabled</c> set and <c>ElementState.Disabled</c> on it,
///         and a hundred assertions about either would have passed.
///     </para>
///     <para>
///         The second half of the defect is that <c>:disabled</c> only muted the text colour, which
///         on a primary button is white text on a blue background going slightly less white. A
///         disabled control has to be obviously unavailable at a glance or the affordance is a lie.
///     </para>
///     <para>
///         ⚠ These read colours off the computed style rather than off pixels. Both would work; the
///         cascade is where the bug was, so that is where the assertion is sharpest — a pixel test
///         would also fail if somebody changed the palette, and would say "the picture moved"
///         instead of "the hover rule reaches disabled controls".
///     </para>
/// </remarks>
public class DisabledStateTests {
    static UiTest Opened() =>
        ControlHarness.Open(
            160f,
            120f,
            """
            button, toggle-button { width: 100px; height: 32px; }
            checkbox { width: 24px; height: 24px; }
            """
        );

    [Fact]
    public void A_disabled_button_does_not_light_up_under_the_pointer() {
        using var ui = Opened();
        var button = ui.Add<Button>("go");
        button.Label = "Go";
        ui.Frame();

        var rest = ui.ColorOf(button, "background-color");

        ui.Get("#go").Hover();
        var hovered = ui.ColorOf(button, "background-color");

        // The premise: hovering an enabled button does change it. Without this the test below could
        // pass against a theme with no hover rule at all.
        Assert.NotEqual(rest, hovered);

        button.Disabled = true;
        ui.Frame();

        // ⚠ The pointer has not moved. It is still over the button, which is exactly the situation
        // that produced the bug — a control disabled while the cursor rests on it.
        ui.Get("#go").ShouldHaveState(ElementState.Hover | ElementState.Disabled);

        Assert.NotEqual(hovered, ui.ColorOf(button, "background-color"));
    }

    [Fact]
    public void A_disabled_button_does_not_go_darker_when_it_is_pressed() {
        using var ui = Opened();
        var button = ui.Add<Button>("go");
        button.Disabled = true;
        ui.Frame();

        var idle = ui.ColorOf(button, "background-color");

        // A disabled button is still hit-testable — it covers its own rectangle and takes the press,
        // it simply does nothing with it. That is what makes the `:active` rule reachable at all.
        ui.Get("#go").Press();

        Assert.Equal(idle, ui.ColorOf(button, "background-color"));
    }

    [Fact]
    public void A_disabled_control_is_visibly_faded() {
        using var ui = Opened();
        var button = ui.Add<Button>("go");
        button.Label = "Go";
        ui.Frame();

        var enabled = ui.Ink();

        button.Disabled = true;
        ui.Frame();

        // ⚠ Against the picture, and the whole picture, because "faded" is a claim about how much
        // light comes off the control rather than about any one property. A theme that expressed it
        // as opacity, as a muted palette or as both satisfies this; one that expressed it only in
        // the text colour of a button whose text is one word does not.
        Assert.True(ui.Ink() < enabled, "a disabled control should be dimmer than an enabled one");
        Assert.True(ui.NumberOf(button, "opacity") is < 1f, "and it should say so as opacity");
    }

    [Fact]
    public void The_same_holds_for_the_other_controls_that_take_a_pointer() {
        using var ui = Opened();

        var box = ui.Add<CheckBox>("agree");
        var toggle = ui.Add<ToggleButton>("bold");
        ui.Frame();

        foreach (var control in new Control[] { box, toggle }) {
            control.Disabled = true;
        }

        ui.Frame();

        // One rule for the whole set rather than one per control, so a control added later inherits
        // the behaviour instead of inheriting the bug.
        foreach (var control in new Control[] { box, toggle }) {
            var idle = ui.ColorOf(control, "background-color");

            ui.MovePointer(control.AbsoluteLeft + 2f, control.AbsoluteTop + 2f);
            ui.Frame();

            Assert.Equal(idle, ui.ColorOf(control, "background-color"));
            Assert.True(ui.NumberOf(control, "opacity") is < 1f, $"{control.Tag} should be faded");
        }
    }
}
