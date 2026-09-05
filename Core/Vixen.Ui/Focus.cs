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
    /// <summary>The element that is part-way through answering "will you give the focus up".</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A resignation is asked once, and without this it was asked twice for the one
    ///         handler shape this feature exists for.</b> A commit-on-blur control ends its edit by
    ///         removing itself from inside its own losing event; <see cref="Remove" /> clears the
    ///         focus it finds, and the focus it finds is still this element because
    ///         <see cref="Focus(UiElement,bool)" /> deliberately writes nothing until the handler
    ///         has returned. So the nested call asked the same element the same question again,
    ///         from underneath the answer.
    ///     </para>
    ///     <para>
    ///         Every commit-on-blur control in this tree survived that only by guarding — a rename
    ///         editor testing whether the row still has one — and a control written without the
    ///         guard committed its value twice. The guard belongs here, once, rather than in each of
    ///         them: an element that is answering the question is not asked it again, and the
    ///         change it is being asked about still happens.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Saved and restored rather than cleared</b>, because the handler is free to move
    ///         the focus again and the element resigning underneath is not always the same one.
    ///     </para>
    /// </remarks>
    UiElement? resigning;

    /// <summary>The element the keyboard is talking to.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from <see cref="KeySurface" /> rather than stored, and it used to be one
    ///     field for the whole document.</b> The focus lives on the surface that holds it — see
    ///     <see cref="UiSurface.Focused" /> — so a second window keeps its own caret while the user
    ///     is in the first, and this reads the one the window manager says the user is looking at.
    ///     With no key surface named it is the primary's, which is what every single-window
    ///     application has always meant by "the focus".
    /// </remarks>
    public UiElement? Focused => Home().Focused;

    /// <summary>The surface a focus question is about when nothing names one.</summary>
    /// <remarks>
    ///     The key window if the window manager has named one, and the primary otherwise. ⚠ Not
    ///     <see cref="Primary" /> alone: the whole point of a per-surface focus is that "the focus"
    ///     means the window being typed into.
    /// </remarks>
    UiSurface Home() => keySurface ?? surfaces[0];

    /// <summary>The surface an element is shown in, without the disposal check <see cref="SurfaceOf" /> makes.</summary>
    /// <remarks>
    ///     ⚠ <b>Reached from teardown, which is why it is not <see cref="SurfaceOf" />.</b> Removing
    ///     a subtree releases the focus that pointed into it, and a document being disposed removes
    ///     its tree — so the public walk's <c>ThrowIfDisposed</c> would turn the last tidy-up into an
    ///     exception. An element with no surface above it is the primary's by construction: only a
    ///     surface root is marked, and every other element is under one.
    /// </remarks>
    UiSurface Holding(UiElement element) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk.SurfaceRoot is { } surface) {
                return surface;
            }
        }

        return surfaces[0];
    }

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
    public UiElement? CommandFocus => Home().CommandFocus;

    /// <summary>Forgets the command focus if it is inside a subtree that is going away.</summary>
    /// <remarks>
    ///     ⚠ Every surface, not the key one. A window can be closed while another is key, and the
    ///     origin left behind would be an element outside the document — which is what
    ///     <see cref="UiElement.Document" /> throws on.
    /// </remarks>
    void ReleaseCommandFocus(UiElement removed) {
        foreach (var surface in surfaces) {
            for (var origin = surface.CommandFocus; origin is not null; origin = origin.Parent) {
                if (ReferenceEquals(origin, removed)) {
                    surface.CommandFocus = null;
                    break;
                }
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
    ///     told apart from a refusal. <c>false</c> also covers the two things a handler can do
    ///     instead of refusing — take the focus somewhere else and keep taking it back, or remove
    ///     the element this was going to give it to.
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
    public bool Focus(UiElement? element, bool force = false) =>
        Focus(element is null ? Home() : Holding(element), element, force);

    /// <summary>Moves the focus within one window.</summary>
    /// <param name="surface">The window whose focus is being moved.</param>
    /// <param name="element">The element, which must be in that surface, or <c>null</c>.</param>
    /// <param name="force">As <see cref="Focus(UiElement,bool)" />.</param>
    /// <returns>Whether the focus is now where it was asked for.</returns>
    /// <remarks>
    ///     ⚠ <b>Focusing something in a background window does not make that window key.</b> The
    ///     window manager decides which window the user is in and says so through
    ///     <see cref="KeySurface" />; an application arranging its own second window is not the
    ///     user clicking on it, and a focus call that raised a window would make every
    ///     <c>Focus(…)</c> in a construction path steal the keyboard.
    /// </remarks>
    internal bool Focus(UiSurface surface, UiElement? element, bool force) {
        if (element is not null && !element.Focusable) {
            return false;
        }

        // The elements this call has already asked to resign, and nothing at all until one of them
        // answers by moving the focus itself. See the restart below.
        List<UiElement>? asked = null;

        while (true) {
            // ⚠ True rather than `element is not null`, and the difference is the whole of what a
            // caller can now conclude. The answer means "the focus is where you asked for it", so
            // clearing an already-clear focus succeeds — the old `false` was indistinguishable from
            // a refusal, which was harmless while nothing could refuse and is a real bug the moment
            // something can.
            if (ReferenceEquals(surface.Focused, element)) {
                return true;
            }

            var previous = surface.Focused;

            // ⚠ Raised before anything is written, which is the point of it: an element asked to
            // give the focus up after it has already gone is being told, not asked. This is the same
            // event the tree would have heard afterwards and not a second one — a duplicate "lost"
            // would be worse than no veto at all.
            // ⚠ And not asked at all when it is already answering. See `resigning`: a handler that
            // ends the edit by removing its own element re-enters here through `Remove` → `Release`
            // with the focus still on it, and the old code put the same question to it a second
            // time. The move itself still happens — this suppresses the ask, not the change.
            if (previous is not null && !ReferenceEquals(previous, resigning)) {
                var leaving = new FocusEvent { Gained = false, Previous = previous, Next = element };

                var outer = resigning;
                resigning = previous;

                try {
                    previous.Raise(leaving);
                } finally {
                    resigning = outer;
                }

                // ⚠ The refusal is read only when the move is one somebody asked for. `force` is how
                // removal and teardown say they are not asking, and without it a field with an
                // invalid value could refuse to be deleted — leaving the document holding a focus
                // that points into a subtree it has just detached.
                if (leaving.Cancel && !force) {
                    return false;
                }

                // ⚠ **The handler runs while the old state is still written, so it can change that
                // state — and the state this call was about to write is then a photograph of a world
                // that has gone.** A rename editor committing on its way out is exactly this: it
                // removes itself and focuses the tree, from inside its own losing event. Carrying
                // on would restate an element that has left the document — a use-after-removal that
                // throws — and would overwrite the focus the handler had just moved.
                //
                // So the whole decision is taken again against what is now true. The `asked` list is
                // what stops two handlers that each re-focus the other from spinning here for ever;
                // it is allocated only when a handler has actually moved the focus, which is close
                // to never.
                if (!ReferenceEquals(surface.Focused, previous)) {
                    asked ??= [];

                    if (asked.Contains(previous)) {
                        return false;
                    }

                    asked.Add(previous);

                    continue;
                }
            }

            // ⚠ And the other half of the same trap, on the other side: a losing handler is allowed
            // to tear down the subtree the focus was going to. Writing it would leave `Focused`
            // pointing at a removed element, which is the state every later read throws on.
            if (element is { IsRemoved: true }) {
                return false;
            }

            return Give(surface, previous, element);
        }
    }

    /// <summary>Writes a focus change that has been agreed to.</summary>
    /// <param name="surface">The window whose focus is moving.</param>
    /// <param name="previous">What had it, read after the last handler ran rather than before.</param>
    /// <param name="element">What is getting it.</param>
    /// <returns><c>true</c>, so the one caller reads as a decision followed by its consequence.</returns>
    bool Give(UiSurface surface, UiElement? previous, UiElement? element) {
        surface.Focused = element;

        // ⚠ The one place the command route's origin is written, and it is deliberately *not* every
        // focus change. See `CommandFocus`.
        if (element is null || !element.IsInCommandTransparentSubtree) {
            surface.CommandFocus = element;

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
    /// <param name="surface">The window the press landed in.</param>
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
    void Defocus(UiSurface surface, UiElement? target, UiElement? focused) {
        // Nothing to take away, or the route has already moved the focus itself and is entitled to
        // the last word — including moving it to nothing.
        //
        // ⚠ Compared against *that* surface's focus rather than the document's. A click on the
        // background of a window the user has just switched to would otherwise be read against the
        // window they left, and would clear a caret in a window nobody clicked in.
        if (focused is null || !ReferenceEquals(surface.Focused, focused) || Captured is not null) {
            return;
        }

        for (var element = target; element is not null; element = element.Parent) {
            if (element.Focusable) {
                return;
            }
        }

        Focus(surface, null, false);
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

    /// <summary>Moves the focus off an element that has just been hidden, and says where it lands.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Skipping a hidden element in <see cref="Collect(UiElement, List{UiElement})" /> fixed the walk and left the
    ///         element that was <i>already focused</i> when it was hidden exactly where it was.</b>
    ///         That is not a corner: it is what a pool does while somebody is typing. Panning a node
    ///         canvas parks the <c>NodeItem</c> whose port box has the caret, and the caret stayed in
    ///         it — a <c>display: none</c> element holding the keyboard, with <c>:focus-within</c>
    ///         still lit on every ancestor and a screen reader announcing something nobody can see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The next Tab was the visible half.</b> <see cref="MoveFocus(FocusDirection)" /> finds its place
    ///         by <c>IndexOf(Focused)</c> in the order, and a hidden element is no longer in it — so
    ///         the index was <c>-1</c> and Tab restarted from the top of the document. A user who
    ///         panned a canvas and pressed Tab was thrown to the first control in the window, which
    ///         reads as the focus having been lost rather than as a pool having tidied up.
    ///     </para>
    ///     <para>
    ///         <b>To the nearest ancestor that can hold it, and only to nothing when there is
    ///         none.</b> The web's answer here is the document body — focus is simply lost — and it
    ///         is the wrong one for a pooled interface: the ancestor a parked element hangs from is
    ///         the thing that parked it, and it is usually focusable itself. The canvas takes the
    ///         keyboard back from its own port box, so the arrow keys keep working; a browser would
    ///         have dropped the user out of the graph entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Forced, so the leaving element cannot veto.</b> A focus veto is a control saying
    ///         "not yet" about a move somebody asked for — a field with an invalid value refusing to
    ///         be left. Nobody asked for this one, and an element that is no longer on the screen
    ///         does not get to keep the keyboard by refusing to let go of it.
    ///     </para>
    /// </remarks>
    void Reseat() {
        // ⚠ Every surface, and not only the key one. A style change that hides a control is a pass
        // over the whole document; a background window whose focus had been hidden would otherwise
        // keep pointing at an element the tab order can no longer see, and would hand the keyboard
        // to it the moment the user switched back.
        for (var i = 0; i < surfaces.Count; i++) {
            Reseat(surfaces[i]);
        }
    }

    /// <inheritdoc cref="Reseat()" />
    void Reseat(UiSurface surface) {
        if (surface.Focused is not { } focused || Reachable(focused)) {
            return;
        }

        for (var candidate = focused.Parent; candidate is not null; candidate = candidate.Parent) {
            if (candidate.Focusable && Reachable(candidate)) {
                Focus(surface, candidate, true);
                return;
            }
        }

        Focus(surface, null, true);
    }

    /// <summary>Whether the tab order can see an element, which is the rule <see cref="Collect(UiElement, List{UiElement})" /> walks.</summary>
    /// <remarks>
    ///     ⚠ <b>Stated once and asked from both places, because two copies of this would drift.</b>
    ///     <see cref="Collect(UiElement, List{UiElement})" /> expresses it as a descent — it returns early on an undisplayed
    ///     element and never reaches the children — and this has to express the same rule as a climb,
    ///     since it starts at the element and does not know what is above it. The asymmetry between
    ///     the two hiding rules is why it cannot be one test: <c>display</c> takes the subtree with
    ///     it and so is asked of every ancestor, <c>visibility</c> is inherited and a descendant may
    ///     declare itself back, so it is asked of this element alone.
    /// </remarks>
    static bool Reachable(UiElement element) {
        if (element.IsStyleHidden) {
            return false;
        }

        for (var ancestor = element; ancestor is not null; ancestor = ancestor.Parent) {
            if (ancestor.IsUndisplayed) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The innermost focus scope containing the focus, or the window it is in.</summary>
    /// <remarks>
    ///     ⚠ <b>A surface root ends the climb, which makes Tab window-local.</b> A surface root's
    ///     parents run on to the document root — deliberately, since that is what keeps one style
    ///     tree and lets a routed event reach the control that opened the window — so without this a
    ///     Tab pressed in a torn-off inspector would walk into the main window's tab order and hand
    ///     it the keyboard, leaving <see cref="Focused" /> reading the key window's <c>null</c> and
    ///     the next Tab starting from the top again. Identical for a single-window document, where
    ///     the primary's root <i>is</i> <see cref="Root" />.
    ///     <para>
    ///         ⚠ <b>Two clauses, and either alone covers the plain case</b> — sabotaging one leaves
    ///         <c>Tab_in_one_window_does_not_walk_into_another</c> green and only removing both turns
    ///         it red, which is recorded here rather than left to be rediscovered. They are not
    ///         redundant: the fallback answers when nothing above the focus declares a scope, and the
    ///         <c>SurfaceRoot</c> test is what stops the climb reaching a focus scope that lives
    ///         <i>above</i> the surface root — the element that owns a torn-off panel, which is
    ///         usually inside the dialog or menu the panel was torn off from.
    ///     </para>
    /// </remarks>
    UiElement Scope() {
        for (var element = Focused; element is not null; element = element.Parent) {
            if (element.IsFocusScope || element.SurfaceRoot is not null) {
                return element;
            }
        }

        return Home().Root;
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
