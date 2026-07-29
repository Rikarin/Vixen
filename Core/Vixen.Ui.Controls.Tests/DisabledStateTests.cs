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

    /// <summary>A field that will not take a keystroke says so in its text colour.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third state, and it had no picture at all.</b> <see cref="Control.Disabled" />
    ///         has <c>:disabled</c>; <see cref="TextField.ReadOnly" /> had nothing — so a field the
    ///         inspector had made read-only because the member has no setter looked exactly like one
    ///         you could type in, and the only way to find out was to type in it and watch nothing
    ///         happen. That is the same class of defect as the hover rules above: the state was
    ///         right and no assertion about the state could see the bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Muted and <i>not</i> faded, which is the difference between the two states.</b> A
    ///         read-only field still takes the focus and its text can still be selected and copied —
    ///         it is meant to be read — so the opacity that says "out of reach" would be a lie about
    ///         what it does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_read_only_field_is_greyed_without_being_faded() {
        using var ui = ControlHarness.Open(200f, 80f, "textbox { width: 160px; }");

        var box = ui.Add<TextBox>("name");
        box.Value = "Crate";
        ui.Frame();

        var editable = ui.ColorOf(box, "color");

        box.ReadOnly = true;
        ui.Frame();

        Assert.NotEqual(editable, ui.ColorOf(box, "color"));

        // ⚠ The text element as well as the field. It inherits its colour, which is exactly the sort
        // of thing a later rule on `field-text` would silently take back.
        Assert.NotEqual(editable, ui.ColorOf(ui.Get("field-text").Element, "color"));

        // And no fade: a read-only field is meant to be read.
        Assert.True(ui.NumberOf(box, "opacity") is null or 1f, "a read-only field should not be faded");
    }

    /// <summary>An empty field that has the focus still draws a caret.</summary>
    /// <remarks>
    ///     ⚠ <b>The one field with no visible sign of the focus was the one you were about to type
    ///     your first character into.</b> `UiElement.Block` answers null for an element with no text,
    ///     so the caret was skipped — and a click gives `Focus` rather than `FocusVisible`, so the
    ///     ring is not drawn either. Between the two, clicking an empty search box looked exactly
    ///     like clicking nothing, which is what "the search does not take the focus" was.
    /// </remarks>
    [Fact]
    public void An_empty_field_draws_a_caret_when_it_has_the_focus() {
        using var ui = ControlHarness.Open(200f, 80f, "search-box { width: 160px; }");

        var box = ui.Add<SearchBox>("filter");
        box.Placeholder = null;
        ui.Frame();

        var unfocused = ui.Capture();

        ui.Document.Focus(box);
        ui.Frame();

        Assert.True(box.IsFocused);
        Assert.Null(box.Value);

        // ⚠ Against the picture, and inside the field's own rectangle. The claim is "something
        // appears where the caret goes", and no property on the control says whether one was drawn —
        // which is exactly why the bug survived: every assertion about the focus passed the whole
        // time. Counting differing pixels rather than summing brightness, because the harness's
        // palette is not the editor's and a caret is one pixel wide.
        var focused = ui.Capture();
        var changed = 0;

        for (var y = (int) box.AbsoluteTop; y < (int) (box.AbsoluteTop + box.Height); y++) {
            for (var x = (int) box.AbsoluteLeft; x < (int) (box.AbsoluteLeft + box.Width); x++) {
                var at = ((y * unfocused.Width) + x) * 4;

                if (unfocused.Pixels[at] != focused.Pixels[at]
                    || unfocused.Pixels[at + 1] != focused.Pixels[at + 1]
                    || unfocused.Pixels[at + 2] != focused.Pixels[at + 2]) {
                    changed++;
                }
            }
        }

        Assert.True(changed > 0, "a focused empty field should draw a caret");
    }
}
