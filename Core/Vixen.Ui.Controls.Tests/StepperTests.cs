// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The two arrows, and that they are the field's own arithmetic rather than a second copy.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every one of these drives a real pointer at the arrow rather than calling
///         <see cref="NumericInput.Nudge" />.</b> The interesting half of this control is not what
///         <c>Nudge</c> does — <c>ScrubTests</c> covers that at every magnitude — it is what happens
///         to a press on a button that is inside a control which claims presses on the capture leg.
///         A test that called <c>Nudge</c> would have passed against every arrangement of that,
///         including the ones a person can see are wrong.
///     </para>
///     <para>
///         The expected numbers are written out rather than computed from the expression the control
///         uses, so that a test cannot agree with an implementation by sharing its mistake.
///     </para>
/// </remarks>
public class StepperTests {
    /// <summary>A hundred thousand lux, which is what a directional light is.</summary>
    const double Daylight = 100_000d;

    [Fact]
    public void An_arrow_steps_the_number_it_is_in() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Number = 4d;
        fixture.Update();

        fixture.Click(stepper.IncrementButton);
        Assert.Equal(5d, stepper.Number);

        fixture.Click(stepper.DecrementButton);
        fixture.Click(stepper.DecrementButton);
        Assert.Equal(3d, stepper.Number);
    }

    /// <summary>
    ///     ⚠ The arrows are <see cref="NumericInput.Nudge" /> and not <c>Number += Step</c>, which is
    ///     the difference nobody notices until a light is being adjusted.
    /// </summary>
    /// <remarks>
    ///     A hundred thousand plus one is what a stepper written the obvious way does to a
    ///     directional light: an arrow that moves a value by a thousandth of a percent is an arrow
    ///     that does nothing. The proportional rate is the field's, and inheriting it is the whole
    ///     argument for the arrows living on the field rather than beside it.
    /// </remarks>
    [Fact]
    public void The_step_is_the_field_s_own_proportional_one() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Decimals = 3;
        stepper.Number = Daylight;
        fixture.Update();

        fixture.Click(stepper.IncrementButton);

        // A hundredth of a hundred thousand, exactly as one press of Up gives.
        Assert.Equal(101_000d, stepper.Number);
    }

    /// <summary>Shift multiplies here because it multiplies on the keys, off the same event.</summary>
    [Fact]
    public void Shift_makes_one_press_worth_ten() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Number = 4d;
        fixture.Update();

        fixture.Click(stepper.IncrementButton, ModifierKeys.Shift);

        Assert.Equal(14d, stepper.Number);
    }

    /// <summary>
    ///     The arrow at the end of the range is disabled, and it comes back the moment the number
    ///     moves off it.
    /// </summary>
    [Fact]
    public void An_arrow_at_the_end_of_the_range_is_disabled() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Minimum = 0d;
        stepper.Maximum = 10d;
        stepper.Number = 10d;
        fixture.Update();

        Assert.True(stepper.IncrementButton.Disabled);
        Assert.False(stepper.DecrementButton.Disabled);

        fixture.Click(stepper.DecrementButton);

        Assert.Equal(9d, stepper.Number);
        Assert.False(stepper.IncrementButton.Disabled);
    }

    /// <summary>
    ///     ⚠ And a range that moves under a number the arrows were measured against re-decides
    ///     them, which is what the whole-property hook is for.
    /// </summary>
    [Fact]
    public void Moving_the_range_rather_than_the_number_re_decides_the_arrows() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Number = 4d;
        fixture.Update();

        Assert.False(stepper.DecrementButton.Disabled);

        stepper.Minimum = 4d;
        fixture.Update();

        Assert.True(stepper.DecrementButton.Disabled);
    }

    /// <summary>A field that will not take a keystroke does not take a click on its arrows either.</summary>
    /// <remarks>
    ///     ⚠ The remark that used to sit here said the arrow <i>keys</i> still stepped a read-only
    ///     numeric field and that it was not this control's to fix. It was right on both counts and
    ///     the hole is closed — see
    ///     <c>TextFieldTests.A_read_only_numeric_field_does_not_step_on_a_key</c>, which is the
    ///     keyboard half and lives with the control that owns the handler.
    /// </remarks>
    [Fact]
    public void A_read_only_stepper_does_not_step() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Number = 4d;
        stepper.ReadOnly = true;
        fixture.Update();

        Assert.True(stepper.IncrementButton.Disabled);

        fixture.Click(stepper.IncrementButton);

        Assert.Equal(4d, stepper.Number);

        // And the keys the arrows are a picture of, on the same control — a stepper is a
        // `NumericInput`, so the guard it inherits is the one being asserted.
        fixture.Document.Focus(stepper);
        fixture.Type(InputKey.Up);

        Assert.Equal(4d, stepper.Number);
    }

    /// <summary>The arrows are not tab stops, and the field they are in still is.</summary>
    [Fact]
    public void The_arrows_are_not_in_the_tab_order() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();

        Assert.Equal(-1, stepper.IncrementButton.TabIndex);
        Assert.Equal(-1, stepper.DecrementButton.TabIndex);
        Assert.True(stepper.Focusable);
    }

    /// <summary>⚠ The press belongs to the arrow, and not to the field's scrub gesture.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A drag rather than a click, because a click cannot tell the two apart.</b> A scrub
    ///         that starts on an arrow still <i>clicks</i> it — a button acts on the tap the gesture
    ///         recogniser makes, and the recogniser is fed whether or not the pointer event was
    ///         marked handled — so every number above is the same with the guard and without it. ⚠
    ///         That is exactly what the first version of this file discovered by sabotage: removing
    ///         <c>NumericInput.Presses</c> left eight tests green, which made the hole the tests'
    ///         and not the control's.
    ///     </para>
    ///     <para>
    ///         What a swallowed press really costs is the gesture: the field captures the pointer,
    ///         and a person who presses the arrow and moves the mouse a little — which is what
    ///         pressing an arrow repeatedly looks like — scrubs the value by forty steps instead of
    ///         pressing a button. Forty pixels rather than nine, so the move is past the
    ///         recogniser's slop and cannot be read as a tap either way.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_drag_that_starts_on_an_arrow_is_not_a_scrub() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Number = 4d;
        fixture.Update();

        var bounds = stepper.IncrementButton.Bounds;
        var x = MathF.Round(bounds.X + (bounds.Width * 0.5f));
        var y = MathF.Round(bounds.Y + (bounds.Height * 0.5f));

        fixture.Press(x, y);
        fixture.MovePointer(x - 40f, y);
        fixture.Release(x - 40f, y);

        Assert.Equal(4d, stepper.Number);
    }

    /// <summary>
    ///     ⚠ The press guard is about controls inside the field, not about presses inside the field:
    ///     the box itself still scrubs.
    /// </summary>
    /// <remarks>
    ///     Without this the cheapest fix for a dead arrow — refusing every press the field's own
    ///     capture handler sees — would pass every other test in this file while taking the drag
    ///     gesture away from every inspector row in the editor.
    /// </remarks>
    [Fact]
    public void The_body_of_a_stepper_still_scrubs() {
        using var fixture = new ControlFixture();

        var stepper = fixture.Add<Stepper>();
        stepper.Decimals = 3;
        stepper.Step = 1d;
        stepper.RelativeStep = 0d;
        stepper.Number = 4d;
        fixture.Update();

        var bounds = stepper.Bounds;
        var x = MathF.Round(bounds.X + (bounds.Width * 0.25f));
        var y = MathF.Round(bounds.Y + (bounds.Height * 0.5f));

        fixture.Press(x, y);
        fixture.MovePointer(x + 10f, y);
        fixture.Release(x + 10f, y);

        Assert.Equal(14d, stepper.Number);
    }
}
