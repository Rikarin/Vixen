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

/// <summary>A pointer doing something somewhere.</summary>
/// <remarks>
///     The position is in <b>document</b> space, not the element's, and stays that way through the
///     whole route. An event whose coordinates changed as it bubbled would mean something different
///     to each handler that read it, and the element it is currently at is already on the event.
/// </remarks>
public sealed class PointerEvent : UiEvent {
    /// <summary>Which pointer. A mouse is one; touches are several at once.</summary>
    public int PointerId { get; init; }

    /// <summary>Its x, in document space.</summary>
    public float X { get; init; }

    /// <summary>Its y, in document space.</summary>
    public float Y { get; init; }

    /// <summary>Which button, for a press or a release.</summary>
    public PointerButton Button { get; init; }

    /// <summary>What happened.</summary>
    public PointerAction Action { get; init; }
}

/// <summary>What a pointer did.</summary>
public enum PointerAction : byte {
    /// <summary>It moved.</summary>
    Moved,

    /// <summary>A button went down.</summary>
    Pressed,

    /// <summary>A button came up.</summary>
    Released
}
