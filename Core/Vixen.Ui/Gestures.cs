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

    /// <summary>What was held on the keyboard when the tap completed.</summary>
    /// <remarks>
    ///     Read off the release rather than the press, because that is the moment the tap became
    ///     one — and because it is the modifier a user who pressed Ctrl <i>while</i> deciding
    ///     expects to have applied.
    /// </remarks>
    public ModifierKeys Modifiers { get; init; }
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

/// <summary>How far a two-finger gesture has got.</summary>
/// <remarks>
///     The same four stages a drag has, and for the same reasons — in particular
///     <see cref="Cancelled" />, which is not <see cref="Completed" />: a cancelled pinch tells a map
///     to go back to the zoom it started at, and a completed one tells it to keep the new one.
/// </remarks>
public enum TransformStage : byte {
    /// <summary>Two fingers have moved far enough to mean it.</summary>
    Started,

    /// <summary>They have moved again.</summary>
    Updated,

    /// <summary>One of them came up.</summary>
    Completed,

    /// <summary>Something else took the pointers, or the target went away.</summary>
    Cancelled
}

/// <summary>Two pointers moving relative to each other: a pinch, a rotation, or both at once.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One event carrying both scale and rotation, rather than a pinch event and a rotate
///         event.</b> They are computed from the same pair of pointers on the same frame and cannot
///         occur apart — two fingers that change their separation almost always change their angle a
///         little as well. Split into two streams, every handler that cares about both would have to
///         correlate them, and every handler that cares about one would have to decide what to do
///         about the other. This is what the web does with <c>gesturechange</c>, for the same reason.
///     </para>
///     <para>
///         <b>Cumulative and incremental, both.</b> <see cref="Scale" /> and <see cref="Rotation" />
///         are measured against where the fingers started, which is what a view being transformed
///         wants; the <c>Delta</c> pair is the change since the last event, which is what something
///         integrating a velocity wants. <see cref="DragEvent" /> carries the same pair for the same
///         reason, and deriving either from the other at the call site loses precision to
///         accumulated rounding.
///     </para>
/// </remarks>
public sealed class TransformEvent : UiEvent {
    /// <summary>The pointer that was already down.</summary>
    public int FirstPointerId { get; init; }

    /// <summary>The pointer that joined it.</summary>
    public int SecondPointerId { get; init; }

    /// <summary>How far along the gesture is.</summary>
    public TransformStage Stage { get; init; }

    /// <summary>Halfway between the two pointers, in document space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }

    /// <summary>How far that midpoint moved since the last event.</summary>
    public float DeltaX { get; init; }

    /// <summary>Ditto.</summary>
    public float DeltaY { get; init; }

    /// <summary>How far apart the pointers are now, over how far apart they started.</summary>
    /// <remarks>One at the start, two when they are twice as far apart, a half when half.</remarks>
    public float Scale { get; init; }

    /// <summary>How far the line between them has turned since the start, in radians, clockwise.</summary>
    /// <remarks>
    ///     ⚠ Accumulated across the whole gesture rather than reduced into a half turn, so a finger
    ///     spun twice round reports four pi rather than zero. A knob that read a wrapped angle would
    ///     jump a full turn every time the fingers passed the wrap point.
    /// </remarks>
    public float Rotation { get; init; }

    /// <summary>How much <see cref="Scale" /> was multiplied by since the last event.</summary>
    public float DeltaScale { get; init; }

    /// <summary>How much <see cref="Rotation" /> changed since the last event.</summary>
    public float DeltaRotation { get; init; }

    /// <summary>What was held at the time.</summary>
    public ModifierKeys Modifiers { get; init; }
}

/// <summary>The thresholds that separate one gesture from another.</summary>
/// <param name="LongPress">How long a press has to stay down and still to become a long press.</param>
/// <param name="MultiTapInterval">How long after a tap a second one still counts as a double.</param>
/// <param name="TouchSlop">
///     How far a pointer may wander before a tap becomes a drag — and, for two pointers, how far
///     their separation may change before it becomes a pinch.
/// </param>
/// <param name="MultiTapSlop">
///     How far apart two taps may land and still be a double tap. Larger than
///     <paramref name="TouchSlop" />, because a finger lifted and put down again is less accurate
///     than a finger that never left.
/// </param>
/// <param name="RotationSlop">
///     How far the line between two pointers may turn, in radians, before it becomes a rotation.
///     Five degrees — small enough that a deliberate twist is caught immediately, and large enough
///     that two fingers dragging a map in parallel do not slowly rotate it because one hand is
///     steadier than the other.
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
    float MultiTapSlop,
    float RotationSlop
) {
    /// <summary>The platform conventions.</summary>
    public static GestureSettings Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(300),
        8f,
        16f,
        MathF.PI / 36f
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
///         <b>One pointer produces taps, long presses and drags; two produce a transform.</b> State
///         is kept per pointer id, so two fingers on two different controls are two independent
///         gestures — which is right. Two fingers that start moving <i>relative to each other</i>
///         become one <see cref="TransformEvent" /> instead, and the drags they had already begun are
///         <see cref="DragStage.Cancelled" /> rather than left running: a map that both panned and
///         zoomed from the same two fingers would move twice as far as either gesture asked for.
///     </para>
///     <para>
///         ⚠ <b>Two, not more.</b> A third finger arriving during a transform is ignored rather than
///         folded in — the two that started it keep it. Three-finger gestures are a separate idea
///         with no agreed meaning across platforms, and averaging an arbitrary number of pointers
///         into one scale is the kind of approximation that is worse than the gap.
///     </para>
/// </remarks>
public sealed class GestureRecognizer {
    readonly Dictionary<int, Press> presses = [];
    Tap? lastTap;
    Transform? transform;

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
                Pair();
                break;

            case PointerAction.Moved when presses.TryGetValue(args.PointerId, out var moving):
                moving.CurrentX = args.X;
                moving.CurrentY = args.Y;

                // The transform gets first refusal. Until it starts it declines, so the two fingers
                // are still ordinary drags — and when it does start it cancels them, which is what
                // DragStage.Cancelled is for.
                if (!Transformed(args)) {
                    Move(args, moving);
                }

                break;

            case PointerAction.Released when presses.Remove(args.PointerId, out var released):
                if (!Ended(args, released)) {
                    Release(args, released);
                }

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
            // A finger held still as part of a pinch is not a long press on whatever is under it,
            // which is what two fingers resting on a map while the other one moves looks like.
            if (press.Dragging || press.LongPressed || press.Suppressed
                || now - press.Started < Settings.LongPress) {
                continue;
            }

            press.LongPressed = true;
            press.Target.Raise(new LongPressEvent { PointerId = press.PointerId, X = press.LastX, Y = press.LastY });
        }
    }

    /// <summary>Ends the current run of taps, so that the next one counts as the first.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What a control calls after acting on a double tap, and without it the gesture
    ///         after an activation is miscounted.</b> A run continues while taps keep landing close
    ///         together in space and time, which is exactly what "double-click a folder, then
    ///         double-click the first thing inside it" produces — the content moved under a pointer
    ///         that did not, so the second gesture arrives as taps three and four and a control
    ///         looking for two never fires.
    ///     </para>
    ///     <para>
    ///         The recogniser cannot know this on its own: whether the thing under the pointer
    ///         changed is the control's answer, not a question about the pointer. So the control that
    ///         consumed the double tap is the one that says the run is over.
    ///     </para>
    /// </remarks>
    public void EndTapRun() => lastTap = null;

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

        // A two-finger gesture that loses a finger to something else is cancelled rather than
        // completed, and for the reason the drag below is: a completed pinch tells a map to keep its
        // new zoom, and this one was never finished.
        if (transform is { Started: true } active
            && (pointerId == active.First.PointerId || pointerId == active.Second.PointerId)) {
            Raise(active, TransformStage.Cancelled, active.LastScale, active.LastRotation, ModifierKeys.None);
            transform = null;
            return true;
        }

        if (transform is { } candidate
            && (pointerId == candidate.First.PointerId || pointerId == candidate.Second.PointerId)) {
            transform = null;
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

        // ⚠ Silently, like the presses above. A transform whose target is inside the subtree being
        // removed has nowhere to send a cancellation, and one whose *pointers* have gone would carry
        // two presses that are no longer in the dictionary — reading live positions off either is
        // reading a gesture nobody is performing.
        if (transform is { } active
            && (!presses.ContainsKey(active.First.PointerId)
                || !presses.ContainsKey(active.Second.PointerId)
                || Contains(element, active.Target))) {
            transform = null;
        }
    }

    static bool Contains(UiElement ancestor, UiElement element) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Notices that two fingers are down and gets ready to watch them.</summary>
    /// <remarks>
    ///     A candidate, not a gesture. Nothing is raised until they move relative to each other, so
    ///     two fingers resting on a list still scroll it as two ordinary drags.
    /// </remarks>
    void Pair() {
        if (transform is not null || presses.Count != 2) {
            return;
        }

        var pair = presses.Values.OrderBy(press => press.Started).ToArray();
        var first = pair[0];
        var second = pair[1];

        // ⚠ The nearest common ancestor, not the first finger's target. Two fingers pinching a map
        // land on two different tiles, and a gesture delivered to one tile is one the map never
        // hears about — the enclosing element is the only one both fingers are agreeing about.
        if (Ancestor(first.Target, second.Target) is not { } target) {
            return;
        }

        transform = new Transform(target, first, second, Separation(first, second), Angle(first, second));
    }

    /// <summary>Feeds a move to the two-finger gesture, if there is one and it wants it.</summary>
    /// <returns>Whether the transform consumed the event.</returns>
    bool Transformed(PointerEvent args) {
        if (transform is not { } active
            || (args.PointerId != active.First.PointerId && args.PointerId != active.Second.PointerId)) {
            return false;
        }

        var distance = Separation(active.First, active.Second);
        var angle = Angle(active.First, active.Second);

        // ⚠ Unwrapped against the previous angle rather than against the start. Atan2 comes back in
        // (-π, π], so a gesture that turns past half a turn would otherwise jump a full turn — and a
        // knob bound to it would spin backwards once per revolution.
        var rotation = active.Rotation + Wrap(angle - active.Angle);
        active.Angle = angle;

        // Two fingers at the same point have no separation and no meaningful scale. Holding the
        // start distance at a floor is what stops a scale of infinity reaching a handler.
        var scale = distance / MathF.Max(active.StartSeparation, 1e-3f);

        if (!active.Started) {
            if (MathF.Abs(distance - active.StartSeparation) < Settings.TouchSlop
                && MathF.Abs(rotation) < Settings.RotationSlop) {
                active.Rotation = rotation;
                return false;
            }

            // Whatever these two fingers had been doing separately, they are not doing it any more.
            Abandon(active.First);
            Abandon(active.Second);

            active.Started = true;
            active.Rotation = rotation;
            Raise(active, TransformStage.Started, scale, rotation, args.Modifiers);
            return true;
        }

        active.Rotation = rotation;
        Raise(active, TransformStage.Updated, scale, rotation, args.Modifiers);
        return true;
    }

    /// <summary>Ends a two-finger gesture when one of its pointers comes up.</summary>
    /// <returns>Whether the release belonged to a transform and should go no further.</returns>
    /// <remarks>
    ///     ⚠ <b>A pointer the transform swallowed produces no tap</b>, even after the gesture has
    ///     ended and even if it never moved far. Two fingers pinching and then lifting is not also
    ///     two taps, and a control that opened something on the second lift would fire at the end of
    ///     every zoom.
    /// </remarks>
    bool Ended(PointerEvent args, Press released) {
        if (transform is not { } active) {
            return released.Suppressed;
        }

        if (args.PointerId != active.First.PointerId && args.PointerId != active.Second.PointerId) {
            return released.Suppressed;
        }

        var started = active.Started;

        if (started) {
            Raise(
                active,
                TransformStage.Completed,
                Separation(active.First, active.Second) / MathF.Max(active.StartSeparation, 1e-3f),
                active.Rotation,
                args.Modifiers
            );
        }

        transform = null;

        // The finger still down keeps its suppression: a pinch that ends with one finger lifted must
        // not turn into a drag by the other, which is what a hand relaxing off a screen looks like.
        return started || released.Suppressed;
    }

    /// <summary>Stops a press being a drag, a long press or a tap, cancelling it if it had begun.</summary>
    static void Abandon(Press press) {
        if (press.Dragging) {
            Raise(press, DragStage.Cancelled, press.CurrentX, press.CurrentY);
            press.Dragging = false;
        }

        press.Suppressed = true;
    }

    static void Raise(Transform active, TransformStage stage, float scale, float rotation, ModifierKeys modifiers) {
        var x = (active.First.CurrentX + active.Second.CurrentX) / 2f;
        var y = (active.First.CurrentY + active.Second.CurrentY) / 2f;

        active.Target.Raise(new TransformEvent {
            FirstPointerId = active.First.PointerId,
            SecondPointerId = active.Second.PointerId,
            Stage = stage,
            X = x,
            Y = y,
            DeltaX = x - active.LastX,
            DeltaY = y - active.LastY,
            Scale = scale,
            Rotation = rotation,
            DeltaScale = active.LastScale <= 0f ? 1f : scale / active.LastScale,
            DeltaRotation = rotation - active.LastRotation,
            Modifiers = modifiers
        });

        active.LastX = x;
        active.LastY = y;
        active.LastScale = scale;
        active.LastRotation = rotation;
    }

    /// <summary>The nearest element that contains both, or <c>null</c> if they are in different trees.</summary>
    static UiElement? Ancestor(UiElement first, UiElement second) {
        for (var candidate = first; candidate is not null; candidate = candidate.Parent) {
            for (var walk = second; walk is not null; walk = walk.Parent) {
                if (ReferenceEquals(candidate, walk)) {
                    return candidate;
                }
            }
        }

        return null;
    }

    static float Separation(Press first, Press second) {
        var dx = second.CurrentX - first.CurrentX;
        var dy = second.CurrentY - first.CurrentY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    static float Angle(Press first, Press second) =>
        MathF.Atan2(second.CurrentY - first.CurrentY, second.CurrentX - first.CurrentX);

    /// <summary>Brings an angle difference into (-π, π], so a turn past half a circle does not jump.</summary>
    static float Wrap(float radians) {
        while (radians > MathF.PI) {
            radians -= 2f * MathF.PI;
        }

        while (radians <= -MathF.PI) {
            radians += 2f * MathF.PI;
        }

        return radians;
    }

    void Move(PointerEvent args, Press press) {
        // A pointer the two-finger gesture took over is not a drag any more, and must not become one
        // again when it wanders further.
        if (press.Suppressed) {
            return;
        }

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

        press.Target.Raise(
            new TapEvent {
                PointerId = args.PointerId,
                Count = count,
                X = args.X,
                Y = args.Y,
                Modifiers = args.Modifiers
            }
        );
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

        /// <summary>Where the pointer is now, whatever it has or has not become.</summary>
        /// <remarks>
        ///     ⚠ Separate from <see cref="LastX" />, which is where the last <i>drag event</i> was
        ///     raised and only moves when one is. A two-finger gesture needs live positions for both
        ///     fingers before either has become a drag, and reading LastX for that measures the
        ///     separation between two places the fingers went down rather than where they are.
        /// </remarks>
        public float CurrentX { get; set; } = x;

        /// <summary>Ditto.</summary>
        public float CurrentY { get; set; } = y;

        public TimeSpan Started { get; } = started;

        public bool Dragging { get; set; }

        public bool LongPressed { get; set; }

        /// <summary>Whether a two-finger gesture has taken this pointer over.</summary>
        /// <remarks>
        ///     One-way, like the drag slop and for the same reason: a finger that has been part of a
        ///     pinch must not become a tap when it lifts, however still it has been since.
        /// </remarks>
        public bool Suppressed { get; set; }
    }

    /// <summary>Two pointers being watched for a pinch or a rotation.</summary>
    /// <remarks>
    ///     Holds the two presses rather than copies of their positions, so it reads live coordinates
    ///     and cannot drift out of step with the pointers it is about.
    /// </remarks>
    sealed class Transform(UiElement target, Press first, Press second, float separation, float angle) {
        public UiElement Target { get; } = target;

        public Press First { get; } = first;

        public Press Second { get; } = second;

        public float StartSeparation { get; } = separation;

        /// <summary>The angle at the previous event, for unwrapping the next one against.</summary>
        public float Angle { get; set; } = angle;

        /// <summary>The accumulated turn since the start, unwrapped.</summary>
        public float Rotation { get; set; }

        public bool Started { get; set; }

        public float LastX { get; set; } = (first.CurrentX + second.CurrentX) / 2f;

        public float LastY { get; set; } = (first.CurrentY + second.CurrentY) / 2f;

        public float LastScale { get; set; } = 1f;

        public float LastRotation { get; set; }
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
