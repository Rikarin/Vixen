// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Taps, long presses and drags read out of the pointer stream.</summary>
/// <remarks>
///     Every time here is written down rather than measured, which is the whole reason the recogniser
///     takes a timestamp instead of reading a clock. A suite that had to sleep for half a second to
///     see a long press would be a suite nobody runs.
/// </remarks>
public class GestureTests {
    static (UiDocument Document, UiElement Target) Surface() {
        var document = new UiDocument(100f, 100f);

        document.Load("root { width: 100px; height: 100px; }");
        document.Update();

        return (document, document.Root);
    }

    static PointerEvent At(PointerAction action, float x, float y, int milliseconds, int pointerId = 0) =>
        new() {
            Action = action,
            X = x,
            Y = y,
            PointerId = pointerId,
            Button = PointerButton.Primary,
            Timestamp = TimeSpan.FromMilliseconds(milliseconds)
        };

    static List<T> Record<T>(UiElement element) where T : UiEvent {
        var events = new List<T>();
        element.AddHandler<T>((_, args) => events.Add(args));

        return events;
    }

    [Fact]
    public void A_press_and_release_in_the_same_place_is_a_tap() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 40));

        var tap = Assert.Single(taps);
        Assert.Equal(1, tap.Count);
        Assert.Equal(10f, tap.X);
    }

    [Fact]
    public void The_first_tap_of_the_session_is_not_a_double_tap() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        // At time zero and at the origin of the clock, which is where an application that measures
        // from process start actually begins. ⚠ This does not catch the missing null check: the count
        // is derived as `previous.Count + 1` and a default previous tap has a count of zero, so the
        // answer is one either way. The rule is asserted here; the guard that expresses it is
        // labelled in the source as unobservable rather than as defended.
        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 0));

        Assert.Equal(1, Assert.Single(taps).Count);
    }

    [Fact]
    public void A_second_tap_soon_and_nearby_counts_up() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 20));
        document.Dispatch(At(PointerAction.Pressed, 12f, 11f, 120));
        document.Dispatch(At(PointerAction.Released, 12f, 11f, 140));

        // Both are reported, and the second says it is the second. A handler that wants only doubles
        // says so; one that wants every tap is already right — which is the point of a count rather
        // than a separate event.
        Assert.Equal([1, 2], taps.Select(static tap => tap.Count));
    }

    [Fact]
    public void A_second_tap_too_late_starts_again() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 20));
        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 900));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 920));

        Assert.Equal([1, 1], taps.Select(static tap => tap.Count));
    }

    [Fact]
    public void A_second_tap_too_far_away_starts_again() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 20));
        document.Dispatch(At(PointerAction.Pressed, 80f, 80f, 60));
        document.Dispatch(At(PointerAction.Released, 80f, 80f, 80));

        // Two taps in quick succession at opposite corners are two people, or one person changing
        // their mind. Time alone would call it a double tap and open a rename on the wrong thing.
        Assert.Equal([1, 1], taps.Select(static tap => tap.Count));
    }

    [Fact]
    public void Moving_past_the_slop_turns_a_press_into_a_drag() {
        var (document, target) = Surface();
        using var owner = document;

        var drags = Record<DragEvent>(target);
        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 12f, 10f, 10));
        document.Dispatch(At(PointerAction.Moved, 40f, 10f, 20));
        document.Dispatch(At(PointerAction.Moved, 50f, 20f, 30));
        document.Dispatch(At(PointerAction.Released, 50f, 20f, 40));

        // The two-pixel wobble is not a drag; a hand resting on a trackpad produces one on every
        // click, and reporting it would make every button drag itself a little.
        Assert.Equal(
            [DragStage.Started, DragStage.Moved, DragStage.Completed],
            drags.Select(static drag => drag.Stage)
        );

        Assert.Empty(taps);
    }

    [Fact]
    public void A_drag_that_comes_back_is_still_a_drag() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 60f, 10f, 20));
        document.Dispatch(At(PointerAction.Moved, 10f, 10f, 40));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 60));

        // ⚠ Slop is one-way. Every flick that overshoots and settles ends near where it started, and
        // a recogniser that asked "how far is it from the press *now*" would fire a tap at the end of
        // a scroll — which is a list that scrolls and then opens whatever stopped under the finger.
        Assert.Empty(taps);
    }

    [Fact]
    public void A_drag_reports_the_step_and_the_journey_separately() {
        var (document, target) = Surface();
        using var owner = document;

        var drags = Record<DragEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 30f, 10f, 10));
        document.Dispatch(At(PointerAction.Moved, 45f, 25f, 20));

        var last = drags[^1];

        // The delta is since the last event; the total is since the press. Summing the deltas gives
        // the total here and would not for a drag that doubled back, which is why both are carried.
        Assert.Equal(15f, last.DeltaX);
        Assert.Equal(15f, last.DeltaY);
        Assert.Equal(35f, last.TotalX);
        Assert.Equal(15f, last.TotalY);
    }

    [Fact]
    public void A_press_that_stays_down_and_still_becomes_a_long_press() {
        var (document, target) = Surface();
        using var owner = document;

        var presses = Record<LongPressEvent>(target);
        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));

        document.Gestures.Tick(TimeSpan.FromMilliseconds(200));
        Assert.Empty(presses);

        document.Gestures.Tick(TimeSpan.FromMilliseconds(600));
        Assert.Single(presses);

        // Once only, however many frames go by with the finger still down.
        document.Gestures.Tick(TimeSpan.FromMilliseconds(900));
        Assert.Single(presses);

        // And the finger coming up afterwards is the end of the long press, not a tap as well —
        // otherwise a context menu opens and the thing underneath it is activated too.
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 1000));
        Assert.Empty(taps);
    }

    [Fact]
    public void A_press_that_has_become_a_drag_never_becomes_a_long_press() {
        var (document, target) = Surface();
        using var owner = document;

        var presses = Record<LongPressEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 60f, 10f, 20));
        document.Gestures.Tick(TimeSpan.FromMilliseconds(900));

        // Holding still at the end of a scroll is not a long press on whatever the finger stopped
        // over. The drag is what this pointer is doing, and it stays what it is doing.
        Assert.Empty(presses);
    }

    [Fact]
    public void A_gesture_goes_to_where_it_started_however_far_the_pointer_travels() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: row; }
            half { width: 50px; height: 100px; }
        """);

        var left = document.Root.Add("half");
        var right = document.Root.Add("half");
        document.Update();

        var onLeft = Record<DragEvent>(left);
        var onRight = Record<DragEvent>(right);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 80f, 10f, 20));
        document.Dispatch(At(PointerAction.Released, 90f, 10f, 40));

        // The same rule as pointer capture and here for the same reason: a drag that leaves the
        // scrollbar it started on has to keep reaching the scrollbar. Note that nothing captured the
        // pointer — a gesture remembers its own target, so the two mechanisms coexist rather than
        // one being a special case of the other.
        Assert.Equal([DragStage.Started, DragStage.Completed], onLeft.Select(static drag => drag.Stage));
        Assert.Empty(onRight);

        // The move that crossed the slop is the one that started the drag rather than a move on top
        // of it, which is why there are two events here and not three: a control that positions
        // something under the pointer gets a coordinate from the first event it sees.
        Assert.Equal(80f, onLeft[0].X);
    }

    [Fact]
    public void Two_pointers_are_two_gestures() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0, pointerId: 1));
        document.Dispatch(At(PointerAction.Pressed, 80f, 80f, 5, pointerId: 2));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 20, pointerId: 1));
        document.Dispatch(At(PointerAction.Released, 80f, 80f, 25, pointerId: 2));

        // ⚠ Two taps rather than one two-finger gesture. State is per pointer id, which is right for
        // everything one finger can do on its own and is not a pinch — combining pointers is owed
        // rather than approximated, and this test says which of the two it currently is.
        Assert.Equal(2, taps.Count);
        Assert.Equal([1, 2], taps.Select(static tap => tap.PointerId));
    }

    [Fact]
    public void A_cancelled_drag_is_not_a_completed_one() {
        var (document, target) = Surface();
        using var owner = document;

        var drags = Record<DragEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 60f, 10f, 20));

        Assert.True(document.Gestures.Cancel(0));

        // A control that treats the two alike drops whatever it was carrying wherever the pointer
        // happened to be when the window lost focus.
        Assert.Equal([DragStage.Started, DragStage.Cancelled], drags.Select(static drag => drag.Stage));

        // And there is nothing left to cancel, so a second attempt says so rather than repeating it.
        Assert.False(document.Gestures.Cancel(0));
    }

    [Fact]
    public void A_press_on_nothing_starts_nothing() {
        var (document, target) = Surface();
        using var owner = document;

        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 400f, 400f, 0));
        document.Dispatch(At(PointerAction.Released, 400f, 400f, 20));

        Assert.Empty(taps);
    }

    [Fact]
    public void Handling_the_pointer_event_does_not_swallow_the_gesture() {
        var (document, target) = Surface();
        using var owner = document;

        target.AddHandler<PointerEvent>((_, args) => args.Handled = true);
        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Released, 10f, 10f, 20));

        // Handling a press means "I dealt with this press". It does not mean the press stopped being
        // part of a tap, and a control that wanted both would otherwise have to reimplement the
        // state machine to get the second one back.
        Assert.Single(taps);
    }

    [Fact]
    public void The_thresholds_are_the_applications_to_choose() {
        var (document, target) = Surface();
        using var owner = document;

        document.Gestures.Settings = document.Gestures.Settings with { TouchSlop = 40f };

        var drags = Record<DragEvent>(target);
        var taps = Record<TapEvent>(target);

        document.Dispatch(At(PointerAction.Pressed, 10f, 10f, 0));
        document.Dispatch(At(PointerAction.Moved, 40f, 10f, 20));
        document.Dispatch(At(PointerAction.Released, 40f, 10f, 40));

        // Thirty pixels is a drag under the desktop default and a wobble on a touchscreen held in one
        // hand. The number is a guess either way, so it is a parameter rather than a constant.
        Assert.Empty(drags);
        Assert.Single(taps);
    }
}
