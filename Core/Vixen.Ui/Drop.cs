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
///         <b>Both representations are on the event <i>and</i> reachable as a
///         <see cref="DataObject" />.</b> An OS drag-in cannot negotiate: the source is another
///         process, the flavours were decided before this application was involved, and what arrives
///         is a path or a string — so <see cref="Files" /> and <see cref="Text" /> are the honest
///         shape for it and stay. ⚠ <see cref="Data" /> is <i>materialised from them on demand</i>
///         rather than being a second thing a producer has to fill in, which is what lets one
///         <c>on:drop</c> handler read a file dragged out of Finder and a row dragged out of a list
///         in the same line of code.
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
    DataObject? data;

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

    /// <summary>What was dropped, as formats a handler can ask for by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Materialised from <see cref="Files" /> and <see cref="Text" /> when nothing set
    ///     it</b>, so a producer that only knows about the two OS representations does not have to
    ///     know this exists, and a handler written against this never has to ask which kind of drag
    ///     it was. An in-app drag sets it directly, and then <see cref="Files" /> is empty and
    ///     <see cref="Text" /> is whatever the source offered under <see cref="DataFormats.Text" />.
    /// </remarks>
    public DataObject Data {
        get => data ??= Represent();
        init => data = value;
    }

    /// <summary>The element the drag started on, or <see langword="null" /> if it came from outside.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="UiEvent.Source" />, which on a routed event is the element the event
    ///     is <i>about</i> — here the drop target.</b> The two are different elements and naming
    ///     this one <c>Source</c> would have shadowed the base property with the opposite meaning.
    ///     <see langword="null" /> is exactly the test for "this came from another application".
    /// </remarks>
    public UiElement? DragSource { get; init; }

    /// <summary>What the target said it would do with it while the drag was over it.</summary>
    /// <remarks>
    ///     <see cref="DropEffect.Copy" /> for an OS drag-in, which never negotiated. For an in-app
    ///     drag it is what the last <see cref="DragOverEvent" /> left in
    ///     <see cref="DragOverEvent.Effect" /> — a source that has to remove the thing it dragged
    ///     reads this to find out whether it was a move.
    /// </remarks>
    public DropEffect Effect { get; init; } = DropEffect.Copy;

    DataObject Represent() {
        var represented = new DataObject();

        if (Files.Count > 0) {
            represented.SetFiles(Files);
        }

        if (Text is { } text) {
            represented.SetText(text);
        }

        return represented;
    }
}

/// <summary>What a drop would do with what is being dragged.</summary>
/// <remarks>
///     <para>
///         <b>A set on the way in and one member on the way out.</b> A source offers what it is
///         willing to have happen — a list row that can be reordered or copied offers
///         <c>Move | Copy</c> — and each target answers with the single one it would perform, which
///         is what a cursor shows and what the source reads afterwards to decide whether to delete
///         the original. Flags rather than two enums because the two vocabularies are the same one.
///     </para>
///     <para>
///         ⚠ <b><see cref="None" /> is a refusal and is the one value that means something is
///         wrong.</b> A target that leaves it there gets no <see cref="DropEvent" /> at all, which
///         is how a drop target refuses one particular payload without giving up being a target.
///     </para>
/// </remarks>
[Flags]
public enum DropEffect : byte {
    /// <summary>Nothing. The drop will not happen.</summary>
    None = 0,

    /// <summary>The target takes a copy and the source keeps its own.</summary>
    Copy = 1,

    /// <summary>The target takes it and the source is expected to give it up.</summary>
    Move = 2,

    /// <summary>The target takes a reference to something that stays where it is.</summary>
    Link = 4
}

/// <summary>Where a drag is in its passage over one drop target.</summary>
public enum DragOverStage : byte {
    /// <summary>It has just come onto this target.</summary>
    Entered,

    /// <summary>It has moved while still over it.</summary>
    Moved,

    /// <summary>It has gone somewhere else, or the drag was cancelled.</summary>
    Left
}

/// <summary>A drag passing over a drop target, before anything has been let go.</summary>
/// <remarks>
///     <para>
///         <b>This is the half an OS drag-in cannot have and an in-app drag is mostly made of.</b>
///         The useful part of a drag is not the drop, it is the feedback on the way — a row that
///         opens a gap where the thing would land, a slot that lights up, a cursor that says copy
///         rather than move. All of that needs the payload <i>before</i> the button comes up, which
///         is what <see cref="Data" /> here is for.
///     </para>
///     <para>
///         ⚠ <b>Addressed to one element and it is not the hit-test result.</b> It goes to the
///         nearest ancestor of what is under the pointer that has <see cref="UiElement.AllowDrop" />
///         set, because <see cref="DragOverStage.Entered" /> and <see cref="DragOverStage.Left" />
///         have to be a matched pair —
///         raised on every leaf the pointer crossed they would arrive dozens of times while the
///         pointer travelled across one row of text, and a target that opened a gap on enter would
///         flicker it. It bubbles from there like everything else.
///     </para>
/// </remarks>
public sealed class DragOverEvent : UiEvent {
    /// <summary>Coming on, moving over, or going away.</summary>
    public DragOverStage Stage { get; init; }

    /// <summary>Where the pointer is, in the surface's space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }

    /// <summary>What would be dropped.</summary>
    public DataObject Data { get; init; } = new();

    /// <summary>The element the drag started on.</summary>
    public UiElement? DragSource { get; init; }

    /// <summary>Everything the source is willing to have happen.</summary>
    public DropEffect Allowed { get; init; }

    /// <summary>What this target would do, which it may narrow or refuse.</summary>
    /// <remarks>
    ///     ⚠ <b>It arrives already set to the best of <see cref="Allowed" />, unlike the DOM's
    ///     <c>dragover</c>, which arrives refusing.</b> The web has to start from a refusal because
    ///     every element is a potential target and only <c>preventDefault</c> distinguishes them;
    ///     here <see cref="UiElement.AllowDrop" /> is already that opt-in, so a second one would
    ///     mean a target that declared itself a target and silently was not — this repository's
    ///     commonest defect. Writing <see cref="DropEffect.None" /> is how a target refuses one
    ///     particular payload.
    /// </remarks>
    public DropEffect Effect { get; set; }
}

public partial class UiElement {
    /// <summary>Whether a drag can be let go over this element.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The opt-in, and the only one.</b> A drag looks for the nearest ancestor of what is
    ///         under the pointer that has this set and addresses every
    ///         <see cref="DragOverEvent" /> to it — so a list row declares it once and the eleven
    ///         labels, icons and backgrounds inside the row need to know nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not gate <see cref="DropEvent" /> from <i>outside</i> the
    ///         application.</b> An OS drag-in has no enter and no over — SDL delivers no motion at
    ///         all while the window system owns the pointer — so there is no pass over the tree in
    ///         which a target could have been chosen, and the drop is raised on the hit-test result
    ///         and bubbles as it always did. Setting this and handling <c>on:drop</c> catches both.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial bool AllowDrop { get; set; }
}

/// <summary>A drag that started inside the application and has not been let go yet.</summary>
/// <remarks>
///     ⚠ <b>One per document, not one per pointer.</b> A second finger arriving in the middle of a
///     drag is a gesture the recogniser is already reading as a pinch, and two simultaneous drags
///     with two payloads is a shape no application in this tree wants and every part of which would
///     have to be threaded through the target's events. <see cref="UiDocument.BeginDrag" /> replaces
///     an unfinished one rather than stacking it.
/// </remarks>
public sealed class DragSession {
    internal DragSession(UiElement source, DataObject data, DropEffect allowed) {
        Source = source;
        Data = data;
        Allowed = allowed;
    }

    /// <summary>The element the drag started on.</summary>
    public UiElement Source { get; }

    /// <summary>What is being dragged.</summary>
    public DataObject Data { get; }

    /// <summary>Everything the source is willing to have happen.</summary>
    public DropEffect Allowed { get; }

    /// <summary>The drop target the pointer is over, if any.</summary>
    public UiElement? Target { get; internal set; }

    /// <summary>What that target last said it would do.</summary>
    /// <remarks>
    ///     <see cref="DropEffect.None" /> whenever there is no target, and whenever the target
    ///     refused this payload — which are the same thing to a cursor and to the drop.
    /// </remarks>
    public DropEffect Effect { get; internal set; }
}

public sealed partial class UiDocument {
    DragSession? drag;

    /// <summary>The drag in progress, if one is.</summary>
    /// <remarks>
    ///     What a source's own <c>drag</c> handler reads to draw its ghost, and what a cursor
    ///     provider reads to decide between the copy and the move arrow.
    /// </remarks>
    public DragSession? CurrentDrag => drag;

    /// <summary>Starts an in-app drag carrying a payload.</summary>
    /// <param name="source">The element it started on, which is what a target sees as <see cref="DropEvent.DragSource" />.</param>
    /// <param name="data">What is being dragged, in as many representations as the source can offer.</param>
    /// <param name="allowed">Everything the source is willing to have happen. Refusing everything is not a drag.</param>
    /// <returns>The session, so the caller can watch its <see cref="DragSession.Effect" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="allowed" /> is <see cref="DropEffect.None" />.</exception>
    /// <remarks>
    ///     ⚠ <b>Called from a <c>dragstart</c> handler, not from a press.</b> The gesture recogniser
    ///     is what decides a press has wandered far enough to be a drag rather than a wobble
    ///     (<c>GestureSettings.TouchSlop</c>), and a source that began a session on
    ///     <c>pointerdown</c> would start one for every click in the application.
    /// </remarks>
    public DragSession BeginDrag(UiElement source, DataObject data, DropEffect allowed = DropEffect.Copy) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);

        if (allowed == DropEffect.None) {
            throw new ArgumentException("A drag that allows nothing cannot be dropped.", nameof(allowed));
        }

        // An unfinished session is told it lost the pointer before the new one starts, so a target
        // that opened a gap for the old drag closes it rather than being left holding feedback for
        // something that is no longer happening.
        CancelDrag();

        drag = new DragSession(source, data, allowed);
        return drag;
    }

    /// <summary>Ends the drag with nothing dropped.</summary>
    /// <returns>Whether there was one.</returns>
    /// <remarks>Escape does this, and so does losing the pointer or the window.</remarks>
    public bool CancelDrag() {
        if (drag is null) {
            return false;
        }

        var session = drag;
        drag = null;
        Leave(session);
        return true;
    }

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

    /// <summary>Moves the drag in progress to wherever the pointer now is.</summary>
    /// <remarks>
    ///     ⚠ <b>Hit-tests past <see cref="Captured" />, which nothing else positional does.</b> A
    ///     source almost always captures the pointer when a drag starts — that is how it keeps
    ///     receiving moves once the cursor has left it — and asking the capture where the pointer is
    ///     would answer "on the source", forever, which is exactly the drag that can never be
    ///     dropped anywhere.
    /// </remarks>
    void TrackDrag(UiSurface surface, DragSession session, float x, float y) {
        var over = DropTargetAt(surface, x, y);

        if (!ReferenceEquals(over, session.Target)) {
            Leave(session);
            session.Target = over;

            if (over is null) {
                return;
            }

            session.Effect = Over(session, over, DragOverStage.Entered, x, y);
            return;
        }

        if (over is not null) {
            session.Effect = Over(session, over, DragOverStage.Moved, x, y);
        }
    }

    /// <summary>Lets go, and delivers a <see cref="DropEvent" /> if the target would take it.</summary>
    /// <returns>The element it was dropped on, or <c>null</c> if nothing took it.</returns>
    UiElement? FinishDrag(UiSurface surface, DragSession session, float x, float y) {
        drag = null;

        // The last `dragover` is what decided the effect, and the pointer may have moved between it
        // and the release — so this is re-asked at the position it was actually let go over rather
        // than trusting a reading taken somewhere else.
        TrackDrag(surface, session, x, y);

        if (session.Target is not { } target || session.Effect == DropEffect.None) {
            Leave(session);
            return null;
        }

        // ⚠ The leave runs *first*, so a target that opened a gap has already closed it by the time
        // its own drop handler inserts something into it. A leave afterwards would undo the drop's
        // own arrangement.
        session.Target = null;
        target.Raise(new DragOverEvent {
            Stage = DragOverStage.Left,
            X = x,
            Y = y,
            Data = session.Data,
            DragSource = session.Source,
            Allowed = session.Allowed,
            Effect = session.Effect
        });

        target.Raise(new DropEvent {
            X = x,
            Y = y,
            Data = session.Data,
            Text = session.Data.Text,
            Files = session.Data.Files,
            DragSource = session.Source,
            Effect = session.Effect
        });

        return target;
    }

    static DropEffect Over(DragSession session, UiElement target, DragOverStage stage, float x, float y) {
        var args = new DragOverEvent {
            Stage = stage,
            X = x,
            Y = y,
            Data = session.Data,
            DragSource = session.Source,
            Allowed = session.Allowed,
            Effect = Preferred(session.Allowed)
        };

        target.Raise(args);
        return args.Effect & session.Allowed;
    }

    static void Leave(DragSession session) {
        if (session.Target is not { } target) {
            return;
        }

        session.Target = null;
        session.Effect = DropEffect.None;

        target.Raise(new DragOverEvent {
            Stage = DragOverStage.Left,
            Data = session.Data,
            DragSource = session.Source,
            Allowed = session.Allowed
        });
    }

    UiElement? DropTargetAt(UiSurface surface, float x, float y) {
        for (var element = HitTest(surface, x, y); element is not null; element = element.Parent) {
            if (element.AllowDrop) {
                return element;
            }
        }

        return null;
    }

    /// <summary>Move beats copy beats link, which is what every platform's cursor says.</summary>
    static DropEffect Preferred(DropEffect allowed) {
        if ((allowed & DropEffect.Move) != 0) {
            return DropEffect.Move;
        }

        return (allowed & DropEffect.Copy) != 0 ? DropEffect.Copy : DropEffect.Link;
    }

    /// <summary>Drives the drag in progress from the pointer stream.</summary>
    /// <returns>The element a drop landed on, if this release was one.</returns>
    internal UiElement? PumpDrag(UiSurface surface, PointerEvent args) {
        if (drag is not { } session) {
            return null;
        }

        switch (args.Action) {
            case PointerAction.Moved:
                TrackDrag(surface, session, args.X, args.Y);
                return null;

            case PointerAction.Released:
                return FinishDrag(surface, session, args.X, args.Y);

            default:
                return null;
        }
    }

    /// <summary>Forgets an element that has gone while a drag was over it.</summary>
    /// <remarks>
    ///     ⚠ <b>Silently, with no <see cref="DragOverStage.Left" />.</b> The element is out of the
    ///     tree by the time this runs, so raising on it would route through parents it no longer
    ///     has; and a target that is gone has nothing left to take back.
    /// </remarks>
    internal void ForgetDropTarget(UiElement element) {
        if (drag is not { } session) {
            return;
        }

        // Up from the target rather than a reference test, on `Captured`'s pattern and for its
        // reason: what is removed is a subtree, and the target may be several levels inside the
        // element that went.
        for (var target = session.Target; target is not null; target = target.Parent) {
            if (ReferenceEquals(target, element)) {
                session.Target = null;
                session.Effect = DropEffect.None;
                return;
            }
        }
    }
}
