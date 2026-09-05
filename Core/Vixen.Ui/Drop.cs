// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Something dragged in from outside the application and let go over a window.</summary>
/// <remarks>
///     <para>
///         <b>A file from Finder or Explorer, or a selection dragged out of another application.</b>
///         The operating system produces it, the platform layer forwards it, and it is hit-tested and
///         bubbles exactly as a <see cref="WheelEvent" /> does — so a drop over a list inside a page
///         reaches the list first and the page only if the list did not handle it.
///     </para>
///     <para>
///         ⚠ <b>Both representations are on the event and there is no <c>DataObject</c>, on
///         purpose.</b> A payload type that a source <i>offers</i> and a target <i>negotiates</i> —
///         several flavours, a preferred one, a promise resolved only if the drop is accepted — is
///         the model an <i>in-app</i> drag needs, because there both ends are elements in this tree
///         and the negotiation is the useful part. An OS drag-in has neither end: the source is
///         another process, the flavours were decided before this application was involved, and what
///         arrives is a path or a string. Inventing the negotiated payload here would mean shipping
///         a type whose interesting half no producer can fill in.
///     </para>
///     <para>
///         ⚠ <b>One event per file, and a five-file drop is five of them.</b> SDL 2 posts an
///         <c>SDL_DROPFILE</c> per path with no coordinates and brackets a group with
///         <c>SDL_DROPBEGIN</c>/<c>SDL_DROPCOMPLETE</c>, which the desktop backend does not yet
///         forward — so a handler that creates a document per drop creates five. <see cref="Files" />
///         is a list rather than a string because that is the shape the grouping will arrive in and
///         not because anything fills it with more than one today.
///     </para>
///     <para>
///         ⚠ <b>Not routed to a captured element.</b> Everything else positional consults
///         <c>UiDocument.Captured</c> first, because a pointer with capture belongs to the element
///         that took it. A drag from another application never pressed a button in this one, so
///         there is nothing it could have captured, and honouring a stale capture would deliver a
///         file to whatever was last being dragged inside the window.
///     </para>
/// </remarks>
public sealed class DropEvent : UiEvent {
    /// <summary>Where it was dropped, in the surface's space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }

    /// <summary>The native paths that were dropped, empty if this was text.</summary>
    /// <remarks>
    ///     ⚠ <b>Native paths, not virtual ones.</b> These come from outside anything the engine has
    ///     mounted, so they are what the operating system calls the file and are not resolvable
    ///     through a <c>VirtualFileSystem</c> mount without being imported first.
    /// </remarks>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The text that was dropped, or <see langword="null" /> if this was a file.</summary>
    public string? Text { get; init; }

    /// <summary>When, on the same clock as the rest.</summary>
    public TimeSpan Timestamp { get; init; }
}

public sealed partial class UiDocument {
    /// <summary>Sends a drop to whatever is under it.</summary>
    /// <param name="args">The event, positioned in document space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    public UiElement? Dispatch(DropEvent args) => Dispatch(Primary, args);

    /// <summary>Sends a drop to whatever is under it in one surface.</summary>
    /// <param name="surface">Which window it happened in.</param>
    /// <param name="args">The event, positioned in that surface's space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    /// <remarks>
    ///     ⚠ <b>The surface matters more here than for a pointer.</b> A pointer that is over the
    ///     wrong window is a hover in the wrong place; a file delivered to the wrong window is
    ///     opened by the wrong panel, and the operating system already decided which window it was
    ///     by sending the event with that window's id on it.
    /// </remarks>
    public UiElement? Dispatch(UiSurface surface, DropEvent args) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(args);

        var target = HitTest(surface, args.X, args.Y);
        target?.Raise(args);
        return target;
    }
}
