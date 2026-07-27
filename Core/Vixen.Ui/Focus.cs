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
///     Routed like any other event, so an ancestor can hear that something inside it took the focus
///     without every control having to tell it. That is what a form uses to know which field is
///     current, and what a scroll view uses to bring it into view.
/// </remarks>
public sealed class FocusEvent : UiEvent {
    /// <summary>Whether this element is taking the focus rather than losing it.</summary>
    public bool Gained { get; init; }

    /// <summary>What had the focus before.</summary>
    public UiElement? Previous { get; init; }

    /// <summary>What has it now.</summary>
    public UiElement? Next { get; init; }
}

public sealed partial class UiDocument {
    /// <summary>The element the keyboard is talking to.</summary>
    public UiElement? Focused { get; private set; }

    /// <summary>Moves the focus.</summary>
    /// <param name="element">The element to focus, or <c>null</c> to focus nothing.</param>
    /// <returns>Whether the focus ended up there.</returns>
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
    public bool Focus(UiElement? element) {
        if (element is not null && !element.Focusable) {
            return false;
        }

        if (ReferenceEquals(Focused, element)) {
            return element is not null;
        }

        var previous = Focused;
        Focused = element;

        Restate(previous, element);

        previous?.Raise(new FocusEvent { Gained = false, Previous = previous, Next = element });
        element?.Raise(new FocusEvent { Gained = true, Previous = previous, Next = element });

        return element is not null;
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
    static void Collect(UiElement element, List<UiElement> into) {
        if (element.Focusable) {
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

    static void Restate(UiElement? previous, UiElement? next) {
        for (var element = previous; element is not null; element = element.Parent) {
            element.State &= ~(element == previous ? ElementState.Focus | ElementState.FocusWithin : ElementState.FocusWithin);
        }

        for (var element = next; element is not null; element = element.Parent) {
            element.State |= element == next ? ElementState.Focus | ElementState.FocusWithin : ElementState.FocusWithin;
        }
    }
}
