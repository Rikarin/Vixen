// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What one pixel of drag does to a number, at every magnitude a member can hold.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every one of these drives a real pointer rather than reading
///         <see cref="NumericInput.Step" /> back.</b> The bug this file exists for was not a property
///         holding the wrong value — the property held exactly what the drawer assigned it — it was
///         what the gesture did with it, and a test asserting that <c>Step</c> was set would have
///         passed throughout.
///     </para>
///     <para>
///         The numbers are written out rather than computed from the same expression the control
///         uses, so a test cannot agree with an implementation by sharing its mistake.
///     </para>
/// </remarks>
public class ScrubTests {
    /// <summary>A hundred thousand lux, which is what a directional light is.</summary>
    const double Daylight = 100_000d;

    [Fact]
    public void One_pixel_of_drag_is_a_percentage_of_a_large_number() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Number = Daylight;
        fixture.Update();

        Drag(fixture, field, 1f);

        // A hundredth of a hundred thousand. The old arithmetic gave 100 001 — a thousandth of a
        // percent — which is the whole complaint.
        Assert.Equal(101_000d, field.Number);
    }

    [Fact]
    public void A_small_number_keeps_the_absolute_step_it_was_given() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Step = 1d;
        field.Number = 4d;
        fixture.Update();

        Drag(fixture, field, 1f);

        // ⚠ Five, not 4.04. A percentage of four is less than the step it was given, so the step
        // wins — which is the hand-over that keeps a small member behaving the way it always did.
        Assert.Equal(5d, field.Number);
    }

    [Fact]
    public void A_count_never_acquires_a_fraction() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();

        // No decimals: a cascade count, a sample count, an index.
        field.Decimals = 0;
        field.Number = 4d;
        fixture.Update();

        // Half a pixel at a time, which is what a scaled display delivers and what no amount of
        // choosing a tidy step protects against.
        Drag(fixture, field, 0.5f, 0.5f, 0.5f);

        Assert.Equal(6d, field.Number);
        Assert.Equal(field.Number, Math.Round(field.Number));
        Assert.DoesNotContain('.', field.Value ?? string.Empty);
    }

    [Fact]
    public void A_large_count_moves_by_whole_numbers_too() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();

        // A budget in bytes: large, unbounded, and integral. Both halves have to hold at once.
        field.Decimals = 0;
        field.Number = 100_000d;
        fixture.Update();

        Drag(fixture, field, 3f);

        Assert.Equal(103_000d, field.Number);
        Assert.Equal(field.Number, Math.Round(field.Number));
    }

    [Fact]
    public void A_field_at_zero_still_scrubs_by_its_step() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Step = 0.25d;
        field.Number = 0d;
        fixture.Update();

        // ⚠ Nought times any percentage is nought, so a purely proportional rate would leave this
        // field unmovable for ever — and nought is the value a field is most often dragged away
        // from. The absolute step is the floor precisely so that this works.
        Drag(fixture, field, 10f);

        Assert.Equal(2.5d, field.Number);
    }

    [Fact]
    public void The_rate_is_fixed_when_the_gesture_starts() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Number = Daylight;
        fixture.Update();

        // ⚠ A hundred separate one-pixel moves, not one move of a hundred pixels. A control that
        // re-read its own magnitude on every move would compound — 100 000 × 1.01¹⁰⁰ ≈ 270 481 —
        // and would give a different answer on a machine that delivered a different number of move
        // events for the same physical gesture.
        Drag(fixture, field, Enumerable.Repeat(1f, 100).ToArray());

        Assert.Equal(200_000d, field.Number);
    }

    [Fact]
    public void Dragging_back_the_same_distance_returns_the_value_it_started_from() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Number = Daylight;
        fixture.Update();

        // Out, and back by exactly as much. Reversibility is the property that makes a scrub safe to
        // explore with, and it is the thing a rate derived from the current value destroys.
        Drag(fixture, field, 37f, -37f);

        Assert.Equal(Daylight, field.Number);
    }

    [Fact]
    public void The_modifiers_scale_a_scrub_the_way_they_scale_the_arrows() {
        using var fixture = new ControlFixture();

        var coarse = fixture.Add<NumericInput>();
        coarse.Decimals = 3;
        coarse.Number = Daylight;

        var fine = fixture.Add<NumericInput>();
        fine.Decimals = 3;
        fine.Number = Daylight;
        fixture.Update();

        Drag(fixture, coarse, ModifierKeys.Shift, 1f);
        Drag(fixture, fine, ModifierKeys.Alt, 1f);

        // The same convention the arrow keys already used, rather than a second one invented here.
        Assert.Equal(110_000d, coarse.Number);
        Assert.Equal(100_100d, fine.Number);
    }

    [Fact]
    public void A_modifier_pressed_part_way_through_does_not_change_what_came_before() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Number = Daylight;
        fixture.Update();

        var (x, y) = Start(fixture, field);

        for (var step = 1; step <= 10; step++) {
            fixture.MovePointer(x + step, y);
        }

        fixture.MovePointer(x + 11f, y, ModifierKeys.Shift);
        fixture.Release(x + 11f, y);

        // Ten plain pixels at a thousand, then one coarse pixel at ten thousand. A control that
        // recomputed the whole offset from the pixel count would apply Shift retroactively and land
        // on 210 000, which is the field lurching under the hand the moment the key goes down.
        Assert.Equal(120_000d, field.Number);
    }

    [Fact]
    public void A_field_that_wants_a_fixed_rate_sets_the_relative_step_to_zero() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Step = 1d;
        field.RelativeStep = 0d;
        field.Number = Daylight;
        fixture.Update();

        Drag(fixture, field, 10f);

        // The escape hatch, and it collapses to the old arithmetic exactly rather than to something
        // near it — which is what a grid pitch or a page number needs.
        Assert.Equal(100_010d, field.Number);
    }

    [Fact]
    public void A_drag_too_slow_to_move_a_count_still_arrives_at_the_next_whole_number() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 0;
        field.Step = 0.3d;
        field.RelativeStep = 0d;
        field.Number = 0d;
        fixture.Update();

        // ⚠ Six pixels at three tenths is 1.8, which rounds to two. A control that rounded each move
        // instead of the running total would round three tenths to nothing six times and never move
        // at all — a field that is dead to a slow hand and alive to a fast one.
        Drag(fixture, field, 1f, 1f, 1f, 1f, 1f, 1f);

        Assert.Equal(2d, field.Number);
    }

    [Fact]
    public void The_click_threshold_still_counts_pixels_rather_than_value() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Step = 0.001d;
        field.RelativeStep = 0d;
        field.Number = 0d;
        fixture.Update();

        // Twenty pixels, worth two hundredths. Well past the gesture recogniser's slop and nowhere
        // near it in value — a control that measured the gesture in the units it was editing would
        // call this a click, focus the field and select all of it under a hand that was dragging.
        Drag(fixture, field, 20f);

        Assert.False(field.IsFocused, "a twenty pixel drag was treated as a click");
        Assert.Equal(0.02d, field.Number, 12);
    }

    [Fact]
    public void An_arrow_key_compounds_where_a_drag_does_not() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<NumericInput>();
        field.Decimals = 3;
        field.Number = Daylight;
        fixture.Update();

        fixture.Document.Focus(field);

        fixture.Type(InputKey.Up);
        Assert.Equal(101_000d, field.Number);

        // ⚠ A percent of where it has reached, not of where it started — which is what a key held
        // down has to do to climb, and the opposite of what a single drag has to do to be
        // reversible. The two gestures want different answers and get them.
        fixture.Type(InputKey.Up);
        Assert.Equal(102_010d, field.Number);
    }

    /// <summary>Presses in the middle of a field and answers where the pointer ended up.</summary>
    /// <remarks>
    ///     ⚠ <b>Whole pixels, and not for tidiness.</b> Every delta below is added to this origin, and
    ///     a fractional one would make <c>x + 0.5f - x</c> a value that depends on where the layout
    ///     put the field — so a test asserting on an exact number would be asserting on the theme.
    /// </remarks>
    static (float X, float Y) Start(ControlFixture fixture, NumericInput field) {
        var bounds = field.Bounds;
        var x = MathF.Round(bounds.X + (bounds.Width * 0.5f));
        var y = MathF.Round(bounds.Y + (bounds.Height * 0.5f));

        fixture.Press(x, y);
        return (x, y);
    }

    static void Drag(ControlFixture fixture, NumericInput field, params float[] deltas) =>
        Drag(fixture, field, ModifierKeys.None, deltas);

    /// <summary>Presses, moves by each delta in turn, and releases.</summary>
    /// <remarks>
    ///     ⚠ <b>The deltas are separate moves rather than one jump to the sum.</b> How many events a
    ///     drag is broken into is exactly the thing a rate derived per move gets wrong, so a helper
    ///     that quietly collapsed them would hide the bug it is here to catch. The pointer is
    ///     captured by the press, so the moves may leave the field's bounds.
    /// </remarks>
    static void Drag(ControlFixture fixture, NumericInput field, ModifierKeys modifiers, params float[] deltas) {
        var (x, y) = Start(fixture, field);
        var at = x;

        foreach (var delta in deltas) {
            at += delta;
            fixture.MovePointer(at, y, modifiers);
        }

        fixture.Release(at, y, modifiers);
    }
}
