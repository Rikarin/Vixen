// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Which way round the tab order to go.</summary>
public enum FocusDirection : byte {
    /// <summary>Tab.</summary>
    Next,

    /// <summary>Shift-Tab.</summary>
    Previous
}

/// <summary>The focus arriving at or leaving an element.</summary>
/// <remarks>
///     <para>
///         Routed like any other event, so an ancestor can hear that something inside it took the
///         focus without every control having to tell it. That is what a form uses to know which
///         field is current, and what a scroll view uses to bring it into view.
///     </para>
///     <para>
///         ⚠ <b>The two halves are raised on opposite sides of the change, and they were not.</b>
///         The losing element hears it <i>before</i> <see cref="UiDocument.Focused" /> moves and the
///         gaining one <i>after</i>, which is what makes <see cref="Cancel" /> mean anything: a
///         refusal that arrived after the focus had already left would be a report rather than a
///         veto. So a handler on the losing leg reads the state it is losing, and one on the gaining
///         leg reads the state it has gained; both read the event's own
///         <see cref="Previous" />/<see cref="Next" /> for the other end, which is what every
///         handler in the tree already did.
///     </para>
/// </remarks>
public sealed class FocusEvent : UiEvent {
    /// <summary>Whether this element is taking the focus rather than losing it.</summary>
    public bool Gained { get; init; }

    /// <summary>What had the focus before.</summary>
    public UiElement? Previous { get; init; }

    /// <summary>What has it now.</summary>
    public UiElement? Next { get; init; }

    /// <summary>Set by a losing element to refuse to give the focus up.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>AppKit's <c>resignFirstResponder</c>, and the pattern it exists for is a field that
    ///         will not let go while its value is invalid.</b> The nearest thing available before was
    ///         to clear <see cref="UiElement.Focusable" /> pre-emptively, which is a different rule —
    ///         it stops the element being reached at all, it does not know where the focus was going,
    ///         and it takes the element out of the tab order on the way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read on the losing leg only, and only on a move somebody asked for.</b>
    ///         <see cref="UiDocument.Focus(UiElement, bool)" /> with <c>force</c> does not ask, and
    ///         every path that is not a user's decision passes it: removing the focused element,
    ///         tearing the document down. A refusal that could outlive its own element is how an
    ///         application becomes permanently unfocusable, which is this feature's failure mode in
    ///         every framework that ships it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not <see cref="UiEvent.Handled" />.</b> Handled says somebody acted on the
    ///         event and the ones further along need not; this says the change must not happen, which
    ///         every remaining handler still deserves to hear — a scroll view that reveals the focus
    ///         has to know the focus is staying put.
    ///     </para>
    /// </remarks>
    public bool Cancel { get; set; }
}

public sealed partial class UiDocument {
    /// <summary>The element the keyboard is talking to.</summary>
    public UiElement? Focused { get; private set; }

    /// <summary>The element a command id resolves from — the focus, ignoring the surfaces that show commands.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The same as <see cref="Focused" /> for every ordinary control</b>, and different
    ///         for exactly the elements that declare
    ///         <see cref="UiElement.IsCommandTransparent" />: a menu, a menu bar, a command palette.
    ///         Focusing one of those leaves this pointing at whatever had it before, so the menu
    ///         still shows and runs the focused <i>view</i>'s handler rather than its own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not restored when the menu closes, and must not be.</b> A menu closes by
    ///         leaving the focus on an item that is no longer visible — there is no focus-restore
    ///         machinery in this framework — so a value that tracked <see cref="Focused" /> back out
    ///         again would land on the hidden item. This one never moved.
    ///     </para>
    /// </remarks>
    public UiElement? CommandFocus { get; private set; }

    /// <summary>Forgets the command focus if it is inside a subtree that is going away.</summary>
    void ReleaseCommandFocus(UiElement removed) {
        for (var origin = CommandFocus; origin is not null; origin = origin.Parent) {
            if (ReferenceEquals(origin, removed)) {
                CommandFocus = null;
                return;
            }
        }
    }

    /// <summary>Moves the focus.</summary>
    /// <param name="element">The element to focus, or <c>null</c> to focus nothing.</param>
    /// <param name="force">
    ///     Whether to move it whatever the losing element says. For the paths that are not a user's
    ///     decision — removal, teardown — and for nothing else. See <see cref="FocusEvent.Cancel" />.
    /// </param>
    /// <returns>
    ///     Whether the focus is now where it was asked for. ⚠ <b><c>true</c> for
    ///     <c>Focus(null)</c></b>, which used to answer <c>false</c> on success and so could not be
    ///     told apart from a refusal.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Sets <see cref="ElementState.Focus" /> on the element and
    ///         <see cref="ElementState.FocusWithin" /> on every ancestor, so <c>:focus</c> and
    ///         <c>:focus-within</c> are answered by the cascade rather than by a special case in the
    ///         renderer. A focus ring is then a stylesheet's business, which is where it belongs.
    ///     </para>
    ///     <para>
    ///         The old chain is cleared before the new one is set, so an element in both — the
    ///         common ancestor of the old and new focus, which is most of them — keeps
    ///         <c>:focus-within</c> continuously rather than having it switched off and on again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That ordering currently has no observable effect</b>, and an earlier version of
    ///         this comment claimed it stopped a transition restarting. It does not: state is only
    ///         ever read during <see cref="Update" />, which cannot run part-way through this
    ///         method, so nothing can see the intermediate. Sabotaging the order fails no test
    ///         because there is nothing to fail. Written this way because it is the correct model,
    ///         and said plainly so the paragraph above is not read as a defended claim.
    ///     </para>
    /// </remarks>
    public bool Focus(UiElement? element, bool force = false) {
        if (element is not null && !element.Focusable) {
            return false;
        }

        // ⚠ True rather than `element is not null`, and the difference is the whole of what a caller
        // can now conclude. The answer means "the focus is where you asked for it", so clearing an
        // already-clear focus succeeds — the old `false` was indistinguishable from a refusal, which
        // was harmless while nothing could refuse and is a real bug the moment something can.
        if (ReferenceEquals(Focused, element)) {
            return true;
        }

        var previous = Focused;

        // ⚠ Raised before anything is written, which is the point of it: an element asked to give the
        // focus up after it has already gone is being told, not asked. This is the same event the
        // tree would have heard afterwards and not a second one — a duplicate "lost" would be worse
        // than no veto at all.
        if (previous is not null) {
            var leaving = new FocusEvent { Gained = false, Previous = previous, Next = element };
            previous.Raise(leaving);

            // ⚠ The refusal is read only when the move is one somebody asked for. `force` is how
            // removal and teardown say they are not asking, and without it a field with an invalid
            // value could refuse to be deleted — leaving the document holding a focus that points
            // into a subtree it has just detached.
            if (leaving.Cancel && !force) {
                return false;
            }
        }

        Focused = element;

        // ⚠ The one place the command route's origin is written, and it is deliberately *not* every
        // focus change. See `CommandFocus`.
        if (element is null || !element.IsInCommandTransparentSubtree) {
            CommandFocus = element;

            // Inside the branch, not outside it: a focus change that leaves the route where it was
            // — every press on a menu, on a menu bar and on a toolbar button — cannot have changed
            // any answer, and telling forty items to re-ask would be exactly the churn the
            // coalescing exists to prevent.
            InvalidateCommands();
        }

        // ⚠ Outside that branch and not inside it, which is the opposite of the line above and is
        // the difference between the two consumers. A command surface asks "who would answer this
        // verb", and a focus move into a command-transparent menu deliberately leaves that where it
        // was; a screen reader asks "what has the focus", and the answer is the menu item. Telling
        // it nothing moved would leave it announcing the view the menu was opened over.
        InvalidateAccessibility();

        Restate(previous, element, KeyboardMode);

        element?.Raise(new FocusEvent { Gained = true, Previous = previous, Next = element });

        return true;
    }

    /// <summary>Takes the focus away when a press lands on something that cannot hold it.</summary>
    /// <param name="target">What the press was routed to, or <c>null</c> if it landed on nothing.</param>
    /// <param name="focused">What had the focus when the press landed.</param>
    /// <remarks>
    ///     <para>
    ///         The other half of the rule every control writes the first half of. A control focuses
    ///         itself when it is pressed; nothing was saying what a press on the background means, so
    ///         a field kept the focus — and the caret, and the keyboard — after the user had visibly
    ///         clicked away from it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole ancestor chain, not the element under the pointer.</b> A press on a
    ///         field lands on the part that draws its text, and that part is not focusable; blurring
    ///         on that would take the focus off every control the moment it was clicked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It blurs and never focuses</b>, even when the chain does contain a focusable
    ///         element. Which control a press focuses is the control's decision and some of them
    ///         decline it — a <c>NumericInput</c> is deliberately left unfocused while it is being
    ///         scrubbed, and focusing it here would be read by its own handler as "already editing"
    ///         and the scrub would never start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A press that took the pointer is exempt.</b> Capture is how a control says the
    ///         press began something it is now carrying out — a scrollbar drag, that same scrub —
    ///         and a scrollbar is not focusable, so without this a field would lose its caret and its
    ///         selection because the panel around it was scrolled.
    ///     </para>
    /// </remarks>
    void Defocus(UiElement? target, UiElement? focused) {
        // Nothing to take away, or the route has already moved the focus itself and is entitled to
        // the last word — including moving it to nothing.
        if (focused is null || !ReferenceEquals(Focused, focused) || Captured is not null) {
            return;
        }

        for (var element = target; element is not null; element = element.Parent) {
            if (element.Focusable) {
                return;
            }
        }

        Focus(null);
    }

    /// <summary>Moves the focus one step round the tab order.</summary>
    /// <param name="direction">Which way.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     Within the innermost <see cref="UiElement.IsFocusScope" /> containing the focus, and
    ///     wrapping there rather than escaping — which is what makes a dialog modal to the keyboard.
    ///     With nothing focused, Tab goes to the first stop and Shift-Tab to the last.
    /// </remarks>
    public bool MoveFocus(FocusDirection direction) {
        var order = TabOrder(Scope());
        if (order.Count == 0) {
            return false;
        }

        var current = Focused is null ? -1 : order.IndexOf(Focused);

        var next = current < 0
            ? direction == FocusDirection.Next ? 0 : order.Count - 1
            : (current + (direction == FocusDirection.Next ? 1 : -1) + order.Count) % order.Count;

        return Focus(order[next]);
    }

    /// <summary>The elements Tab visits inside a subtree, in the order it visits them.</summary>
    /// <param name="scope">The subtree.</param>
    /// <returns>The stops, in order.</returns>
    /// <remarks>
    ///     <para>
    ///         HTML's rule, implemented faithfully rather than sanely. A <b>positive</b>
    ///         <see cref="UiElement.TabIndex" /> comes before every zero, in numeric order — so one
    ///         element with <c>1</c> jumps to the front of a form it was written at the bottom of.
    ///         <b>Zero</b> is document order. <b>Negative</b> is focusable but not a stop.
    ///     </para>
    ///     <para>
    ///         The sort is stable, which is not decoration: two elements with the same positive
    ///         index have to stay in document order relative to each other, and an unstable sort
    ///         would give a tab order that changed depending on how many elements were on the page.
    ///     </para>
    /// </remarks>
    public static List<UiElement> TabOrder(UiElement scope) {
        ArgumentNullException.ThrowIfNull(scope);

        var stops = new List<UiElement>();
        Collect(scope, stops);

        // OrderBy is documented as stable; List.Sort is not. That difference is the whole
        // paragraph above.
        return [.. stops.Where(static stop => stop.TabIndex > 0).OrderBy(static stop => stop.TabIndex),
            .. stops.Where(static stop => stop.TabIndex == 0)];
    }

    /// <summary>Gathers the focusable elements of a subtree, in document order.</summary>
    /// <remarks>
    ///     ⚠ Does <i>not</i> filter by tab index. It used to, and that guard was dead: the two
    ///     buckets above select <c>&gt; 0</c> and <c>== 0</c>, so a negative index is in neither and
    ///     is already excluded. Sabotage found it — removing the guard broke nothing — and a
    ///     redundant test in a second place is worse than none, because it makes the reader believe
    ///     the rule lives in two places and keep them in step.
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It does filter by whether the element can be seen, and for a long time it did
    ///         not.</b> <see cref="UiElement.Focusable" /> has no relation to <c>display</c> or
    ///         <c>visibility</c>, so a hidden control was a Tab stop: the ring landed on nothing, and
    ///         the next keystroke went to a control the user could not find. Both of the other two
    ///         walks over this same tree already asked — <c>AccessKeys.Collect</c> refuses a
    ///         zero-boxed subtree and <c>Navigation.FindInDirection</c> refuses an empty
    ///         <see cref="UiElement.Bounds" /> — so the tab order was the outlier of three rather
    ///         than a deliberate exception.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two hiding rules are asked differently because they <i>are</i> different.</b>
    ///         <c>display: none</c> stops the descent, because it is not inherited and takes the
    ///         subtree with it; <c>visibility</c> is asked per element and does not stop it, because
    ///         it is inherited and a descendant that declares <c>visible</c> is back — the same
    ///         asymmetry the draw list and the hit test already have. Collapsing them into one test
    ///         would make a visible island inside a hidden panel unreachable by Tab while it is
    ///         painted and clickable.
    ///     </para>
    ///     <para>
    ///         This is the general case of the one <c>ColorSwatch</c> fixed for itself by clearing
    ///         <c>Selectable</c> on a parked chip, and it is what every other pool on this path —
    ///         the node canvas's port editors, a parked tree row, a parked code line — needed and did
    ///         not have.
    ///     </para>
    /// </remarks>
    static void Collect(UiElement element, List<UiElement> into) {
        if (element.IsUndisplayed) {
            return;
        }

        if (element.Focusable && !element.IsStyleHidden) {
            into.Add(element);
        }

        foreach (var child in element.Children) {
            Collect(child, into);
        }
    }

    /// <summary>The innermost focus scope containing the focus, or the root.</summary>
    UiElement Scope() {
        for (var element = Focused; element is not null; element = element.Parent) {
            if (element.IsFocusScope) {
                return element;
            }
        }

        return Root;
    }

    /// <summary>Moves the focus flags from one chain to another.</summary>
    /// <param name="previous">What had the focus.</param>
    /// <param name="next">What has it now.</param>
    /// <param name="visible">
    ///     Whether the focus should show. See <see cref="KeyboardMode" /> — a ring drawn on every
    ///     click looks like a bug and a ring withheld from a keyboard makes the interface unusable,
    ///     so what decides is how the focus arrived rather than that it did.
    /// </param>
    /// <remarks>
    ///     ⚠ <b><see cref="ElementState.FocusVisible" /> is cleared unconditionally and set
    ///     conditionally.</b> Clearing it only when the new focus does not want it would leave the
    ///     ring behind on an element that a click has just taken it from — the state is per-element
    ///     and the element that keeps it is not the one that was clicked.
    /// </remarks>
    static void Restate(UiElement? previous, UiElement? next, bool visible) {
        for (var element = previous; element is not null; element = element.Parent) {
            element.State &= ~(element == previous
                ? ElementState.Focus | ElementState.FocusVisible | ElementState.FocusWithin
                : ElementState.FocusWithin);
        }

        for (var element = next; element is not null; element = element.Parent) {
            if (element != next) {
                element.State |= ElementState.FocusWithin;
                continue;
            }

            element.State |= visible
                ? ElementState.Focus | ElementState.FocusVisible | ElementState.FocusWithin
                : ElementState.Focus | ElementState.FocusWithin;
        }
    }
}
