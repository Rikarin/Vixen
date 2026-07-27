// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>A press and release that stayed put.</summary>
/// <remarks>
///     ⚠ <b>A double tap raises this twice</b>, with <see cref="Count" /> going 1 then 2, rather
///     than raising a separate event. A double tap is the same gesture with a number on it, and
///     splitting the two forces every handler to answer "is a double tap also two taps" — a question
///     with no general answer, since a button wants both to press it and a rename wants only the
///     second. Reported this way, the handler that cares says so and the one that does not is
///     already right. It is what the web does, for the same reason.
/// </remarks>
public sealed class TapEvent : UiEvent {
    /// <summary>Which pointer.</summary>
    public int PointerId { get; init; }

    /// <summary>How many taps in a row landed in the same place, this one included.</summary>
    public int Count { get; init; }

    /// <summary>Where, in document space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }
}

/// <summary>A press that stayed put and stayed down.</summary>
/// <remarks>
///     The one gesture that fires because nothing happened, which is why
///     <see cref="GestureRecognizer.Tick" /> exists: a recogniser fed only by input events cannot
///     produce it, because there is no input to be fed.
/// </remarks>
public sealed class LongPressEvent : UiEvent {
    /// <summary>Which pointer.</summary>
    public int PointerId { get; init; }

    /// <summary>Where, in document space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }
}

/// <summary>Where a drag is in its life.</summary>
/// <remarks>
///     Named a stage rather than a phase because <see cref="UiEvent.Phase" /> is already the
///     routing phase, and a drag event has both — it is at some point of its own life while being at
///     some point of its journey through the tree.
/// </remarks>
public enum DragStage : byte {
    /// <summary>The pointer has moved far enough to be a drag rather than a wobble.</summary>
    Started,

    /// <summary>It has moved again.</summary>
    Moved,

    /// <summary>It came up.</summary>
    Completed,

    /// <summary>Something took the pointer away — a window losing focus, a system gesture.</summary>
    Cancelled
}

/// <summary>A pointer moving with a button down.</summary>
public sealed class DragEvent : UiEvent {
    /// <summary>Which pointer.</summary>
    public int PointerId { get; init; }

    /// <summary>Where it is in its life.</summary>
    public DragStage Stage { get; init; }

    /// <summary>Where the pointer is, in document space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }

    /// <summary>How far it moved since the last drag event.</summary>
    public float DeltaX { get; init; }

    /// <summary>Ditto.</summary>
    public float DeltaY { get; init; }

    /// <summary>How far it has moved since the press.</summary>
    /// <remarks>
    ///     Carried as well as the delta because the two answer different questions and summing the
    ///     deltas does not give this: a drag that goes out and comes back has a total near zero and a
    ///     path that was not. A scrollbar wants the total; a canvas wants the delta.
    /// </remarks>
    public float TotalX { get; init; }

    /// <summary>Ditto.</summary>
    public float TotalY { get; init; }
}

/// <summary>The thresholds that separate one gesture from another.</summary>
/// <param name="LongPress">How long a press has to stay down and still to become a long press.</param>
/// <param name="MultiTapInterval">How long after a tap a second one still counts as a double.</param>
/// <param name="TouchSlop">How far a pointer may wander before a tap becomes a drag.</param>
/// <param name="MultiTapSlop">
///     How far apart two taps may land and still be a double tap. Larger than
///     <paramref name="TouchSlop" />, because a finger lifted and put down again is less accurate
///     than a finger that never left.
/// </param>
/// <remarks>
///     ⚠ <b>In device-independent pixels and wall-clock time, and both are guesses.</b> The defaults
///     are the platform conventions — half a second for a long press, a third for a double tap — and
///     a touch UI on a small screen will want different ones. They are a parameter rather than a
///     constant so that the argument happens in the application rather than in a patch to this file.
/// </remarks>
public readonly record struct GestureSettings(
    TimeSpan LongPress,
    TimeSpan MultiTapInterval,
    float TouchSlop,
    float MultiTapSlop
) {
    /// <summary>The platform conventions.</summary>
    public static GestureSettings Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(300),
        8f,
        16f
    );
}

/// <summary>Turns a stream of pointer events into taps, long presses and drags.</summary>
/// <remarks>
///     <para>
///         <b>Time arrives on the event rather than from a clock this reads.</b> A recogniser that
///         calls <c>DateTime.Now</c> cannot be tested without sleeping, cannot replay a recorded
///         input trace, and behaves differently when a breakpoint holds the frame — and the caller
///         already knows what time the input happened, which is more accurate than what time this
///         got round to looking.
///     </para>
///     <para>
///         <b>Every gesture goes to the element the press landed on</b>, for its whole life, however
///         far the pointer travels afterwards. That is the same rule as pointer capture and it is
///         here for the same reason: a drag that leaves the scrollbar it started on must keep
///         reaching the scrollbar. They coexist rather than duplicate — capture redirects the raw
///         pointer events, this remembers the target of a gesture already in progress.
///     </para>
///     <para>
///         ⚠ <b>One pointer at a time.</b> State is kept per pointer id, so two fingers produce two
///         independent taps or drags — which is right — but nothing here combines them, so pinch and
///         rotate are owed rather than approximated. Two fingers dragging is currently two drags.
///     </para>
/// </remarks>
public sealed class GestureRecognizer {
    readonly Dictionary<int, Press> presses = [];
    Tap? lastTap;

    /// <summary>Creates a recogniser.</summary>
    /// <param name="settings">The thresholds, or the platform conventions if not given.</param>
    public GestureRecognizer(GestureSettings? settings = null) => Settings = settings ?? GestureSettings.Default;

    /// <summary>The thresholds it recognises against.</summary>
    public GestureSettings Settings { get; set; }

    /// <summary>Feeds it a pointer event.</summary>
    /// <param name="args">The event, positioned in document space.</param>
    /// <param name="target">What the event was routed to, or <c>null</c> if nothing was under it.</param>
    /// <remarks>
    ///     ⚠ Runs whether or not a handler marked the pointer event <see cref="UiEvent.Handled" />.
    ///     Handling a press means "I dealt with this press"; it does not mean the press stopped being
    ///     part of a tap, and a control that consumed the press but wanted the tap would otherwise
    ///     have to reimplement the whole state machine to get it back.
    /// </remarks>
    public void Process(PointerEvent args, UiElement? target) {
        ArgumentNullException.ThrowIfNull(args);

        switch (args.Action) {
            case PointerAction.Pressed when target is not null:
                presses[args.PointerId] = new Press(target, args.PointerId, args.X, args.Y, args.Timestamp);
                break;

            case PointerAction.Moved when presses.TryGetValue(args.PointerId, out var moving):
                Move(args, moving);
                break;

            case PointerAction.Released when presses.Remove(args.PointerId, out var released):
                Release(args, released);
                break;

            default:
                // A move with no button down is a hover, and a press on nothing is not the start of
                // anything. Both are ordinary rather than exceptional.
                break;
        }
    }

    /// <summary>Tells it what time it is, so that a press that is not going anywhere can become a long one.</summary>
    /// <param name="now">The current time, on the same clock as the events.</param>
    /// <remarks>
    ///     Called once a frame. A long press is the absence of input, so nothing in the input stream
    ///     can report it and something outside has to ask.
    /// </remarks>
    public void Tick(TimeSpan now) {
        foreach (var press in presses.Values) {
            if (press.Dragging || press.LongPressed || now - press.Started < Settings.LongPress) {
                continue;
            }

            press.LongPressed = true;
            press.Target.Raise(new LongPressEvent { PointerId = press.PointerId, X = press.LastX, Y = press.LastY });
        }
    }

    /// <summary>Gives up on a pointer, because something else took it.</summary>
    /// <param name="pointerId">Which pointer.</param>
    /// <returns>Whether there was anything to give up on.</returns>
    /// <remarks>
    ///     A cancelled drag is not a completed one, and a control that treats them alike drops
    ///     whatever it was carrying wherever the pointer happened to be when the window lost focus.
    /// </remarks>
    public bool Cancel(int pointerId) {
        if (!presses.Remove(pointerId, out var press)) {
            return false;
        }

        if (press.Dragging) {
            Raise(press, DragStage.Cancelled, press.LastX, press.LastY);
        }

        return true;
    }

    /// <summary>Drops any gesture in progress on an element that is going away.</summary>
    /// <param name="element">The subtree root being removed.</param>
    /// <remarks>
    ///     ⚠ Silently rather than as a cancellation. A cancelled drag tells its target to put back
    ///     whatever it was carrying, and the target is the thing being deleted — raising an event on
    ///     an element mid-removal hands a handler a half-detached tree to react to.
    /// </remarks>
    internal void Forget(UiElement element) {
        foreach (var (id, press) in presses) {
            for (var walk = press.Target; walk is not null; walk = walk.Parent) {
                if (!ReferenceEquals(walk, element)) {
                    continue;
                }

                presses.Remove(id);
                break;
            }
        }
    }

    void Move(PointerEvent args, Press press) {
        // ⚠ Slop is one-way. Once a press has wandered far enough to be a drag it can never be a tap
        // again, even if the pointer comes back to where it started — which it does at the end of
        // every flick that overshoots and settles. A test on the current distance rather than on a
        // latched flag fires a tap at the end of a scroll.
        if (!press.Dragging) {
            var dx = args.X - press.StartX;
            var dy = args.Y - press.StartY;

            if ((dx * dx) + (dy * dy) < Settings.TouchSlop * Settings.TouchSlop) {
                return;
            }

            press.Dragging = true;
            Raise(press, DragStage.Started, args.X, args.Y);
            return;
        }

        Raise(press, DragStage.Moved, args.X, args.Y);
    }

    void Release(PointerEvent args, Press press) {
        if (press.Dragging) {
            Raise(press, DragStage.Completed, args.X, args.Y);
            return;
        }

        // A long press has already been reported and the finger coming up afterwards is the end of
        // it, not a tap as well. Reporting both means a context menu opens and then the thing under
        // it is also activated.
        if (press.LongPressed) {
            return;
        }

        var count = lastTap is { } previous
            && args.Timestamp - previous.When <= Settings.MultiTapInterval
            && Within(previous.X, previous.Y, args.X, args.Y, Settings.MultiTapSlop)
                ? previous.Count + 1
                : 1;

        lastTap = new Tap(args.Timestamp, args.X, args.Y, count);
        press.Target.Raise(new TapEvent { PointerId = args.PointerId, Count = count, X = args.X, Y = args.Y });
    }

    static void Raise(Press press, DragStage stage, float x, float y) {
        press.Target.Raise(new DragEvent {
            PointerId = press.PointerId,
            Stage = stage,
            X = x,
            Y = y,
            DeltaX = x - press.LastX,
            DeltaY = y - press.LastY,
            TotalX = x - press.StartX,
            TotalY = y - press.StartY
        });

        press.LastX = x;
        press.LastY = y;
    }

    static bool Within(float ax, float ay, float bx, float by, float distance) {
        var dx = bx - ax;
        var dy = by - ay;

        return (dx * dx) + (dy * dy) <= distance * distance;
    }

    /// <summary>What a pointer has been doing since it went down.</summary>
    /// <remarks>
    ///     A class rather than a struct because it is mutated in place through a dictionary lookup,
    ///     and a struct would be updated on a copy — the kind of bug that looks like the threshold
    ///     being wrong.
    /// </remarks>
    sealed class Press(UiElement target, int pointerId, float x, float y, TimeSpan started) {
        public UiElement Target { get; } = target;

        public int PointerId { get; } = pointerId;

        public float StartX { get; } = x;

        public float StartY { get; } = y;

        public float LastX { get; set; } = x;

        public float LastY { get; set; } = y;

        public TimeSpan Started { get; } = started;

        public bool Dragging { get; set; }

        public bool LongPressed { get; set; }
    }

    /// <summary>The last tap, for deciding whether the next one is a double.</summary>
    /// <remarks>
    ///     <para>
    ///         Nullable because "there has not been a tap yet" is not "there was a tap at the origin
    ///         at time zero", and an application that measures its clock from process start really
    ///         does open with an event at time zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It makes no observable difference and an earlier version of this comment claimed
    ///         it did.</b> Sabotaging it away fails nothing, because the count is derived as
    ///         <c>previous.Count + 1</c> and a default <see cref="Tap" /> has a count of zero — so
    ///         the first tap of the session comes out as one either way, by arithmetic rather than by
    ///         the guard. Kept because the model is right and because the arithmetic that rescues it
    ///         is a coincidence nobody should have to notice, and said plainly so the paragraph above
    ///         is not read as a defended claim.
    ///     </para>
    /// </remarks>
    readonly record struct Tap(TimeSpan When, float X, float Y, int Count);
}
