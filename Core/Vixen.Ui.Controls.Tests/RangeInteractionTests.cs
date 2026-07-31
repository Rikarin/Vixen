// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Sliders, driven by dragging them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the controls where the element tree tells you nothing.</b> A slider's
///         thumb, its rail and its filled part are not elements — the class's own remarks explain
///         why, and the consequence is that "the thumb moved" is a claim only a picture can settle.
///         Every test here that is about position reads pixels.
///     </para>
///     <para>
///         And every one drags with a real pointer rather than assigning <c>Value</c>. Setting the
///         property tests the property; dragging tests the hit test, the pointer capture, the
///         fraction arithmetic against the inset rail, and the snapping — which is the whole of what
///         a slider is.
///     </para>
/// </remarks>
public class RangeInteractionTests {
    // Wide enough that a thumb's width is small against the rail, so "left half" and "right half"
    // are unambiguous — a 40px control with a 14px thumb has neither.
    const float Width = 200f;

    static UiTest Opened() =>
        ControlHarness.Open(
            Width,
            40f,
            """
            slider, range-slider, progress-bar { width: 200px; height: 24px; }
            spinner { width: 24px; height: 24px; }
            """
        );

    [Fact]
    public void Dragging_the_thumb_moves_the_value_and_the_thumb_with_it() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");

        var changes = new List<float>();
        slider.ValueChanged += (_, value) => changes.Add(value);

        var atRest = ui.InkIn(0, 0, 40, 40);

        ui.Get("#volume").DragTo(Width * 0.75f, 12f);

        // The value followed the pointer, and it got there continuously rather than in one jump —
        // a slider that only updated on release would report one change.
        Assert.True(slider.Value > 0.6f, $"expected the value past 0.6, got {slider.Value}");
        Assert.True(changes.Count > 1, $"expected the value to move while dragging, got {changes.Count} changes");

        // ⚠ And the picture agrees. The thumb is not an element, so this is the only assertion that
        // can tell a slider that moved its thumb from one that moved only its number.
        Assert.True(ui.InkIn(0, 0, 40, 40) < atRest, "the thumb should have left the left edge");
        Assert.True(ui.InkIn(150, 0, 50, 40) > 0, "the thumb should have arrived at the right");
    }

    [Fact]
    public void A_press_jumps_the_thumb_to_where_it_landed() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");

        // Not a drag — a single press, which is what clicking a track does everywhere else.
        ui.Get("#volume").Press();

        Assert.True(slider.Value > 0.4f && slider.Value < 0.6f, $"expected roughly the middle, got {slider.Value}");
    }

    [Fact]
    public void The_drag_keeps_reaching_the_slider_after_the_pointer_leaves_it() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");

        ui.Get("#volume").Press();
        Assert.Same(slider, ui.Document.Captured);

        // Well below the control, which is where a finger goes when somebody is dragging quickly.
        ui.MovePointer(Width * 0.9f, 200f);
        ui.Frame();

        // ⚠ Hit-testing during a drag is the bug capture exists to prevent. Without it the value
        // freezes the moment the pointer leaves the control, which reads as a slider that sticks.
        Assert.True(slider.Value > 0.8f, $"expected the drag to keep tracking, got {slider.Value}");

        ui.ReleasePointer();
        ui.Frame();
        Assert.Null(ui.Document.Captured);
    }

    /// <summary>
    ///     ⚠ <b>Up is more.</b> A vertical fader runs bottom-to-top, which is the one thing the
    ///     coordinate system does not do — and it is not a detail: every mixer, every volume control
    ///     and every hardware desk ever built puts the maximum at the top, so one that grew downwards
    ///     would be read backwards by everybody who touched it.
    /// </summary>
    [Fact]
    public void A_vertical_slider_runs_bottom_to_top() {
        using var ui = ControlHarness.Open(
            120f,
            240f,
            "slider.vertical { width: 24px; height: 200px; }"
        );

        var slider = ui.Add<Slider>("fader");

        slider.Orientation = Orientation.Vertical;
        ui.Frame();

        Assert.True(slider.Height > slider.Width, $"the fader is {slider.Width}×{slider.Height}");

        // Near the top of the control, which on a fader means loud.
        ui.Get("#fader").DragTo(12f, 10f);
        Assert.True(slider.Value > 0.8f, $"dragging to the top should be near the maximum, got {slider.Value}");

        ui.Get("#fader").DragTo(12f, 230f);
        Assert.True(slider.Value < 0.2f, $"dragging to the bottom should be near the minimum, got {slider.Value}");

        // ⚠ And the picture agrees, which is the only assertion that can tell a fader that moved its
        // thumb from one that moved only its number — the thumb is not an element.
        Assert.True(ui.InkIn(0, 160, 120, 80) > 0, "the thumb should have arrived at the bottom");
    }

    [Fact]
    public void A_step_snaps_the_value_and_the_thumb_to_it() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");
        slider.Step = 0.25f;
        ui.Frame();

        ui.Get("#volume").DragTo(Width * 0.62f, 12f);

        // Whatever the pointer asked for, the answer is on the grid.
        Assert.Equal(0f, MathF.IEEERemainder(slider.Value, 0.25f), 0.001f);
    }

    [Fact]
    public void The_value_never_leaves_the_bounds_however_far_the_pointer_goes() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");
        slider.Minimum = -1f;
        slider.Maximum = 1f;
        ui.Frame();

        ui.Get("#volume").DragTo(Width * 4f, 12f);
        Assert.Equal(1f, slider.Value, 0.001f);

        ui.Get("#volume").DragTo(-Width * 4f, 12f);
        Assert.Equal(-1f, slider.Value, 0.001f);
    }

    [Fact]
    public void The_arrows_move_it_and_home_and_end_take_it_to_the_ends() {
        using var ui = Opened();
        var slider = ui.Add<Slider>("volume");
        slider.Step = 0.1f;
        ui.Frame();

        ui.Get("#volume").Focus().ShouldBeFocused();

        ui.PressKey(InputKey.Right);
        ui.Frame();
        Assert.Equal(0.1f, slider.Value, 0.001f);

        ui.PressKey(InputKey.Left);
        ui.Frame();
        Assert.Equal(0f, slider.Value, 0.001f);

        ui.PressKey(InputKey.End);
        ui.Frame();
        Assert.Equal(1f, slider.Value, 0.001f);

        ui.PressKey(InputKey.Home);
        ui.Frame();
        Assert.Equal(0f, slider.Value, 0.001f);
    }

    [Fact]
    public void Hovering_a_slider_sets_the_state_a_stylesheet_reads() {
        using var ui = Opened();
        ui.Add<Slider>("volume");

        ui.Get("#volume").Hover().ShouldHaveState(ElementState.Hover);
    }

    [Fact]
    public void Pressing_it_marks_it_active_until_the_release() {
        using var ui = Opened();
        ui.Add<Slider>("volume");

        ui.Get("#volume").Press().ShouldHaveState(ElementState.Active);

        ui.Get("#volume").Release();
        Assert.Equal(ElementState.None, ui.Get("#volume").Element.State & ElementState.Active);
    }

    [Fact]
    public void A_range_slider_moves_the_thumb_that_was_grabbed() {
        using var ui = Opened();
        var range = ui.Add<RangeSlider>("span");
        range.Low = 0.2f;
        range.High = 0.8f;
        ui.Frame();

        // ⚠ From the low thumb, not from the control's centre. The centre of a 0.2–0.8 span is
        // exactly between the two thumbs, so a drag that started there would grab whichever one the
        // tie-break happened to favour — and the test would be about the tie-break.
        ui.Drag(Width * 0.2f, 12f, Width * 0.4f, 12f);

        Assert.True(range.Low > 0.3f, $"the low thumb should have moved, got {range.Low}");
        Assert.Equal(0.8f, range.High, 0.01f);
    }

    [Fact]
    public void A_range_sliders_thumbs_meet_but_never_cross() {
        using var ui = Opened();
        var range = ui.Add<RangeSlider>("span");
        range.Low = 0.2f;
        range.High = 0.8f;
        ui.Frame();

        // Grab the low thumb and drive it well past the high one.
        ui.Drag(Width * 0.2f, 12f, Width * 0.95f, 12f);

        // ⚠ Stopped rather than swapped. Swapping means the thumb under the cursor is suddenly a
        // different thumb, and the drag that was raising the ceiling starts lowering the floor.
        Assert.True(range.Low <= range.High + 0.001f, $"low {range.Low} passed high {range.High}");
    }

    [Fact]
    public void A_progress_bar_draws_more_as_it_fills_and_takes_no_input() {
        using var ui = Opened();
        var progress = ui.Add<ProgressBar>("loading");

        var empty = ui.Ink();

        progress.Value = 1f;
        ui.Frame();
        var full = ui.Ink();

        Assert.NotEqual(empty, full);

        // Not interactive: a press changes nothing, because a progress bar is a readout.
        ui.Get("#loading").Click();
        Assert.Equal(1f, progress.Value, 0.001f);
    }

    [Fact]
    public void A_spinner_draws_something_and_turns_when_its_phase_does() {
        using var ui = Opened();
        var spinner = ui.Add<Spinner>("busy");
        spinner.Sweep = 0.5f;
        ui.Frame();

        Assert.True(ui.Ink() > 0, "a spinner should draw something");

        // ⚠ <b>Two earlier versions of this measurement could not see the thing being tested.</b> A
        // total over the whole picture is rotation-invariant, so it was identical. A total over the
        // left half is too: a half-turn sweep starting at zero covers the bottom, starting at a half
        // turn covers the top, and both are symmetric about the vertical axis. The top half is the
        // region those two phases actually disagree about — which is worth writing down, because
        // "assert that something changed" is not a test until the statistic can tell.
        var top = ui.InkIn(0, 0, 24, 12);

        spinner.Phase = 0.5f;
        ui.Frame();

        Assert.NotEqual(top, ui.InkIn(0, 0, 24, 12));
    }

    [Fact]
    public void A_slider_with_no_room_neither_draws_nor_divides_by_nothing() {
        using var ui = ControlHarness.Open(
            200f,
            40f,
            // ⚠ `min-width` as well as `width`. The theme gives a slider a minimum, so a rule that
            // set only the width produced a slider of the theme's minimum size — and the first
            // version of this test asserted invisibility against a control that was plainly there.
            "slider { width: 0px; min-width: 0px; height: 24px; }"
        );

        var slider = ui.Add<Slider>("collapsed");

        Assert.Equal(0f, slider.Width, 0.001f);

        // A rail of no width has no inside, and every position in it is equally the start. What
        // matters is that asking does not throw and does not produce a NaN that spreads.
        Assert.Equal(0f, slider.Value, 0.001f);
        Assert.False(float.IsNaN(slider.Value));
        ui.Get("#collapsed").ShouldNotBeVisible();
    }
}
