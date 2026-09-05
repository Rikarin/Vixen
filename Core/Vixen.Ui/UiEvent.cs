// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Which leg of an event's journey a handler is listening on.</summary>
public enum RoutingStrategy : byte {
    /// <summary>Root to target, before the target sees it. For an ancestor that wants first refusal.</summary>
    Capture,

    /// <summary>Target to root, after. What almost everything wants.</summary>
    Bubble,

    /// <summary>The target only, neither before nor after.</summary>
    Direct
}

/// <summary>Where an event currently is on its way through the tree.</summary>
public enum RoutingPhase : byte {
    /// <summary>Descending towards the target.</summary>
    Capture,

    /// <summary>At the element the event is about.</summary>
    Target,

    /// <summary>Climbing back out.</summary>
    Bubble
}

/// <summary>Anything routed through the element tree.</summary>
/// <remarks>
///     <para>
///         Capture down, target, bubble back out — the DOM's model, and it is the right one for the
///         same reason: an ancestor sometimes needs to see an event <i>before</i> its children (a
///         scroll view swallowing a drag) and usually needs to see it after (a button inside a list
///         row that also wants the click). One direction can express neither case without the
///         handler knowing about the other.
///     </para>
///     <para>
///         <see cref="Handled" /> stops the walk for everyone except handlers that asked for
///         handled events too. That exception exists because "did anyone deal with this" is a
///         legitimate question — a focus manager or a diagnostic overlay needs to hear about the
///         click that a button already consumed.
///     </para>
/// </remarks>
public abstract class UiEvent {
    /// <summary>The element the event is about.</summary>
    public UiElement? Source { get; internal set; }

    /// <summary>The element whose handler is running now.</summary>
    public UiElement? Current { get; internal set; }

    /// <summary>Where on the route it is.</summary>
    public RoutingPhase Phase { get; internal set; }

    /// <summary>Whether something has dealt with it.</summary>
    public bool Handled { get; set; }
}

/// <summary>Which button, if any.</summary>
public enum PointerButton : byte {
    /// <summary>None — a move, or a hover.</summary>
    None,

    /// <summary>The primary button.</summary>
    Primary,

    /// <summary>The secondary button.</summary>
    Secondary,

    /// <summary>The middle button.</summary>
    Middle
}

/// <summary>What kind of device a pointer event came from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A fact on the event rather than a convention about its id range.</b> Before this
///         existed, a finger and a mouse arrived as the same shape and were told apart only by
///         which range <c>PlatformInput</c> had allocated their <see cref="PointerEvent.PointerId" />
///         from — a rule written down in one file's comments and knowable nowhere else.
///     </para>
///     <para>
///         ⚠ <b><see cref="Unknown" /> is zero, and that is deliberate rather than tidy.</b> The
///         value exists to be trusted at an arbitration point: <c>touch-action</c> governs touch and
///         nothing else, so a reader that applied it to a mouse would stop a map responding to a
///         mouse drag, which no browser does. A default of <see cref="Mouse" /> would make every
///         producer that has not been updated <i>claim</i> to be a mouse, which is exactly the
///         failure a default cannot be allowed to have — an unset field must read as "nobody said",
///         not as an answer.
///     </para>
/// </remarks>
public enum PointerType : byte {
    /// <summary>Nobody said — an event from a producer that does not know or does not care.</summary>
    Unknown,

    /// <summary>A mouse, a trackpad, or anything else that moves a cursor.</summary>
    Mouse,

    /// <summary>A finger.</summary>
    Touch,

    /// <summary>A stylus.</summary>
    /// <remarks>
    ///     Distinct from <see cref="Touch" /> because the two differ where it matters: a pen is
    ///     precise, so it does not want a finger's enlarged hit target, and it can hover, so it does
    ///     produce the crossings a finger does not.
    /// </remarks>
    Pen
}

/// <summary>A pointer doing something somewhere.</summary>
/// <remarks>
///     The position is in <b>document</b> space, not the element's, and stays that way through the
///     whole route. An event whose coordinates changed as it bubbled would mean something different
///     to each handler that read it, and the element it is currently at is already on the event.
/// </remarks>
public sealed class PointerEvent : UiEvent {
    /// <summary>Which pointer. A mouse is one; touches are several at once.</summary>
    public int PointerId { get; init; }

    /// <summary>What kind of device produced it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not derivable from <see cref="PointerId" />, and that is why it is here.</b> The id
    ///     ranges <c>PlatformInput</c> keeps apart are a collision-avoidance measure, not a device
    ///     taxonomy — a second mouse, or a host that numbers its pens, would break any reader that
    ///     inferred the device from the number. See <see cref="Vixen.Ui.PointerType" /> for why the
    ///     default is <see cref="PointerType.Unknown" /> rather than <see cref="PointerType.Mouse" />.
    /// </remarks>
    public PointerType PointerType { get; init; }

    /// <summary>Its x, in document space.</summary>
    public float X { get; init; }

    /// <summary>Its y, in document space.</summary>
    public float Y { get; init; }

    /// <summary>Which button, for a press or a release.</summary>
    public PointerButton Button { get; init; }

    /// <summary>What was held on the keyboard at the time.</summary>
    /// <remarks>
    ///     On the pointer event because a modified click is one thing rather than two: a list adding
    ///     to its selection on Ctrl-click cannot ask a keyboard what is held <i>now</i> without
    ///     getting the wrong answer for any click it deals with a frame later, and a control that
    ///     tracked the modifiers itself would have to see every key event in the document to do it.
    /// </remarks>
    public ModifierKeys Modifiers { get; init; }

    /// <summary>What happened.</summary>
    public PointerAction Action { get; init; }

    /// <summary>When it happened.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried on the event rather than read from a clock</b> by whoever needs it. A gesture
    ///     recogniser that calls <c>DateTime.Now</c> cannot be tested without sleeping, cannot replay
    ///     a recorded trace, and reports a different gesture when a breakpoint holds the frame. The
    ///     platform layer already knows what time the input happened, which is a better answer than
    ///     what time anything downstream got round to asking.
    ///     <para>
    ///         Measured from whenever the application decided to start counting rather than from an
    ///         epoch, because every question asked of it is a difference between two of them.
    ///     </para>
    /// </remarks>
    public TimeSpan Timestamp { get; init; }
}

/// <summary>What a pointer did.</summary>
public enum PointerAction : byte {
    /// <summary>It moved.</summary>
    Moved,

    /// <summary>A button went down.</summary>
    Pressed,

    /// <summary>A button came up.</summary>
    Released,

    /// <summary>It came onto an element.</summary>
    /// <remarks>
    ///     ⚠ <b>Never fed in from outside</b> — the document works these out for itself from where
    ///     the pointer is, and delivers them <see cref="RoutingStrategy.Direct" /> to each element
    ///     whose hover changed. A backend reports moves, presses and releases; crossing an edge is a
    ///     fact about a tree the backend cannot see.
    /// </remarks>
    Entered,

    /// <summary>It left an element.</summary>
    Exited
}
