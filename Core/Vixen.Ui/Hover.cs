// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>A pointer wheel, or a two-axis trackpad scroll.</summary>
/// <remarks>
///     <para>
///         Its own event rather than a <see cref="PointerAction" />, because it carries a quantity
///         the others do not and because the elements that want it are not the elements that want
///         presses. A scroll view listens for this and for nothing else.
///     </para>
///     <para>
///         ⚠ <b>The deltas are in device-independent pixels, already resolved.</b> A wheel notch is
///         not a pixel and every platform disagrees about how many it is worth; that conversion
///         belongs to the backend, which knows what device produced the event and what the user's
///         system settings say. A UI framework that multiplied notches by a constant of its own
///         would scroll at a different speed from every other application on the machine.
///     </para>
/// </remarks>
public sealed class WheelEvent : UiEvent {
    /// <summary>Which pointer.</summary>
    public int PointerId { get; init; }

    /// <summary>Where the pointer is, in document space.</summary>
    public float X { get; init; }

    /// <summary>Ditto.</summary>
    public float Y { get; init; }

    /// <summary>How far to scroll horizontally, positive meaning towards the content's end.</summary>
    public float DeltaX { get; init; }

    /// <summary>Ditto, vertically.</summary>
    public float DeltaY { get; init; }

    /// <summary>Whether a notched wheel produced this, rather than a continuous surface such as a
    /// trackpad.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only <see langword="true" /> is a claim, and the default is deliberately the
    ///         other one.</b> False means "a continuous device, <i>or</i> a backend that could not
    ///         tell" — so every behaviour that keys off this has to put the unchanged, direct
    ///         manipulation treatment on the false arm. A synthesised event that says nothing about
    ///         its device therefore behaves exactly as every event did before this property existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The distinction is not free, and no backend here gets it for nothing.</b>
    ///         <c>SDL_MouseWheelEvent</c> carries no phase and no device class, so the desktop
    ///         backend reads it off the shape of the delta — an exactly integral precise delta is a
    ///         notch, a fractional one is a surface — and the browser reads
    ///         <c>WheelEvent.deltaMode</c>, which is decisive in one direction only. Both are written
    ///         down where they are made.
    ///     </para>
    ///     <para>
    ///         It matters because the two want opposite treatment. A trackpad flick is direct
    ///         manipulation that the operating system has already given momentum to, so easing it
    ///         again would lag the fingers and compound the deceleration; a wheel notch is a discrete
    ///         request for a distance, with momentum from nowhere, and is the one scroll
    ///         <c>scroll-behavior: smooth</c> is actually about.
    ///     </para>
    /// </remarks>
    public bool Notched { get; init; }

    /// <summary>What was held on the keyboard at the time.</summary>
    /// <remarks>
    ///     Here for the same reason it is on <see cref="PointerEvent" />: a modified wheel is one
    ///     thing rather than two. Ctrl-wheel means zoom in every map, canvas and timeline ever
    ///     written, and a control that had to ask a keyboard what was held <i>now</i> would get the
    ///     wrong answer for any event it dealt with a frame later.
    /// </remarks>
    public ModifierKeys Modifiers { get; init; }

    /// <summary>When, on the same clock as the rest.</summary>
    public TimeSpan Timestamp { get; init; }
}

public sealed partial class UiDocument {
    readonly List<UiElement> hovered = [];
    readonly List<UiElement> pressed = [];
    readonly List<UiElement> chain = [];

    /// <summary>The deepest element the pointer is over, or <c>null</c> if it is over nothing.</summary>
    public UiElement? Hovered => hovered.Count > 0 ? hovered[0] : null;

    /// <summary>Sends a wheel event to whatever is under it.</summary>
    /// <param name="args">The event, positioned in document space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    /// <remarks>
    ///     Hit-tested rather than sent to the focus, and it bubbles — so a wheel over a list inside a
    ///     page reaches the list first and the page only if the list did not handle it. That is what
    ///     makes nested scrolling behave: the innermost thing that can scroll does, and the chaining
    ///     is <see cref="UiEvent.Handled" /> rather than a rule this has to know.
    /// </remarks>
    public UiElement? Dispatch(WheelEvent args) => Dispatch(Primary, args);

    /// <summary>Sends a wheel event to whatever is under it in one surface.</summary>
    /// <param name="surface">Which window it happened in.</param>
    /// <param name="args">The event, positioned in that surface's space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    public UiElement? Dispatch(UiSurface surface, WheelEvent args) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(args);

        var target = Captured ?? HitTest(surface, args.X, args.Y);
        target?.Raise(args);
        return target;
    }

    /// <summary>Brings the hover and press states up to date with where the pointer is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Hover follows the hit test and not the capture.</b> Everything else about a
    ///         pointer with capture goes to the capturing element wherever the pointer is; hover is
    ///         the exception, because <c>:hover</c> is a statement about where the pointer <i>is</i>
    ///         and a scrollbar being dragged does not make the button underneath the cursor hovered.
    ///     </para>
    ///     <para>
    ///         <b>The state goes on the whole ancestor chain</b>, which is what CSS means by
    ///         <c>:hover</c> — a card is hovered while the pointer is over the button inside it, and
    ///         that is what makes <c>.card:hover .button</c> work at all.
    ///     </para>
    /// </remarks>
    void Track(UiSurface surface, PointerEvent args) {
        if (args.Action == PointerAction.Pressed) {
            // A press is the moment the pointer takes over the interaction. Moving a mouse across
            // the screen while somebody tabs through a form is not.
            LeaveKeyboardMode();
        }

        var under = HitTest(surface, args.X, args.Y);

        chain.Clear();
        for (var element = under; element is not null; element = element.Parent) {
            chain.Add(element);
        }

        Restate(hovered, chain, ElementState.Hover, PointerAction.Exited, PointerAction.Entered, args);

        switch (args.Action) {
            case PointerAction.Pressed:
                Restate(pressed, chain, ElementState.Active, null, null, args);
                break;

            case PointerAction.Released:
                chain.Clear();
                Restate(pressed, chain, ElementState.Active, null, null, args);
                break;

            default:
                // A move neither presses nor releases. `:active` stays on whatever the press put it
                // on even as the pointer wanders off it — which is what makes a press you can back
                // out of by releasing elsewhere look right while you are deciding.
                break;
        }
    }

    /// <summary>Moves a state flag from one chain of elements to another, telling the ones that changed.</summary>
    /// <remarks>
    ///     ⚠ <b>The elements in both chains are left alone</b>, which is the whole reason this is a
    ///     difference rather than a clear-then-set. The common ancestors of the old and new hover are
    ///     most of them, and switching <c>:hover</c> off and on again on every one of them each time
    ///     the pointer moves a pixel would restyle the entire path to the root sixty times a second —
    ///     and restart every transition on it.
    /// </remarks>
    static void Restate(
        List<UiElement> previous,
        List<UiElement> next,
        ElementState state,
        PointerAction? left,
        PointerAction? entered,
        PointerEvent args
    ) {
        foreach (var element in previous) {
            if (next.Contains(element)) {
                continue;
            }

            // A removed element is not restyled and not told. It has no style slot to clear and no
            // business hearing that the pointer left something it is no longer part of.
            if (element.IsRemoved) {
                continue;
            }

            element.State &= ~state;

            if (left is { } action) {
                Crossed(element, args, action);
            }
        }

        foreach (var element in next) {
            if (previous.Contains(element)) {
                continue;
            }

            element.State |= state;

            if (entered is { } action) {
                Crossed(element, args, action);
            }
        }

        previous.Clear();
        previous.AddRange(next);
    }

    /// <summary>Tells one element that the pointer crossed its edge.</summary>
    /// <remarks>
    ///     ⚠ <b>Direct rather than routed</b>, and one event per element rather than one that
    ///     bubbles. These are the DOM's <c>mouseenter</c> and <c>mouseleave</c>, not its
    ///     <c>mouseover</c>: a bubbling version tells a menu that the pointer entered something every
    ///     time it moves between two items inside it, and every consumer of these — a tooltip, a
    ///     highlighted row, a drop target — wants "the pointer is now over <i>me</i>" rather than
    ///     "over something under me". The bubbling form is what <see cref="PointerAction.Moved" />
    ///     already is.
    /// </remarks>
    static void Crossed(UiElement element, PointerEvent args, PointerAction action) =>
        EventRouter.Direct(
            element,
            new PointerEvent {
                PointerId = args.PointerId,
                // Carried across, because a crossing is a fact about the pointer that caused it. A
                // synthesised `Entered` that said `Unknown` would be the one event in a touch
                // sequence a `touch-action` reader could not classify.
                PointerType = args.PointerType,
                X = args.X,
                Y = args.Y,
                Button = PointerButton.None,
                Action = action,
                Timestamp = args.Timestamp
            }
        );

    /// <summary>Drops the hover and press states of a subtree about to go.</summary>
    /// <remarks>
    ///     Without this, closing a menu by clicking an item leaves the item in the hover list — and
    ///     the next pointer move walks a list holding an element whose style slot has been handed to
    ///     somebody else, and clears <c>:hover</c> on a stranger.
    /// </remarks>
    void ForgetHover(UiElement element) {
        ForgetHover(hovered, element);
        ForgetHover(pressed, element);
    }

    static void ForgetHover(List<UiElement> tracked, UiElement removed) {
        for (var i = tracked.Count - 1; i >= 0; i--) {
            for (var walk = tracked[i]; walk is not null; walk = walk.Parent) {
                if (!ReferenceEquals(walk, removed)) {
                    continue;
                }

                tracked.RemoveAt(i);
                break;
            }
        }
    }
}
