// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What a mutation told the document about itself.</summary>
/// <remarks>
///     Only the two kinds <see cref="StyleUpdater" /> can narrow. Everything else — a new element, a
///     removal, a move, an inline style, a stylesheet — arrives as <see cref="UiDocument.Invalidate" />
///     and costs a cold pass, which is correct for all of them and cheap for none. Widening this enum
///     is how the remaining ones get their own path.
/// </remarks>
enum StyleChangeKind {
    /// <summary>A class was added or removed.</summary>
    Class,

    /// <summary>The element's pseudo state changed.</summary>
    State
}

/// <summary>One recorded change, replayed against the updater on the next pass.</summary>
/// <param name="Kind">Which kind it was.</param>
/// <param name="Element">The element it happened to.</param>
/// <param name="Name">The class, for <see cref="StyleChangeKind.Class" />; ignored otherwise.</param>
readonly record struct StyleChange(StyleChangeKind Kind, StyleNodeId Element, string? Name);

public sealed partial class UiDocument {
    readonly List<StyleChange> changes = [];

    /// <summary>Whether the next pass has to resolve every element.</summary>
    /// <remarks>
    ///     Starts true, because a document with no styles resolved yet has nothing to be incremental
    ///     against.
    /// </remarks>
    bool cold = true;

    /// <summary>How many elements the last <see cref="Update" /> cascaded.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Per frame and not per pass, which is what makes zero readable.</b> A settled
    ///         document reports nought here, because <see cref="Update" />'s early return clears this
    ///         beside <see cref="StylesApplied" /> — it did not, once, and a no-op frame then reported
    ///         the last real pass's number for ever. See #596: the assertion the name invites was red
    ///         against a document doing nothing whatever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The number Phase 4b's invalidation-minimality gate is about, finally readable from
    ///         the thing an application drives.</b> That gate — toggling one class on a 100×100 grid
    ///         restyles exactly one element — was green for two phases against
    ///         <see cref="StyleUpdater" /> directly, while <see cref="Update" /> called
    ///         <c>StyleEngine.ResolveAll</c> and cascaded all ten thousand.
    ///     </para>
    ///     <para>
    ///         It is deliberately not <see cref="StylesApplied" />, and confusing the two is what hid
    ///         the gap. <see cref="StylesApplied" /> counts elements whose <i>layout style was
    ///         rebuilt</i>, which the interned <see cref="ComputedStyle" /> makes a pointer comparison
    ///         — so it reads 1 for a one-class change however the styles were arrived at, and says
    ///         nothing at all about the cost of arriving at them.
    ///     </para>
    /// </remarks>
    public int StylesResolved { get; private set; }

    /// <summary>Whether the last pass had to resolve every element rather than a few.</summary>
    public bool LastPassWasCold { get; private set; }

    /// <summary>Records a class change, which the updater can narrow.</summary>
    internal void InvalidateClass(StyleNodeId element, string className) {
        dirty = true;

        if (!cold) {
            changes.Add(new StyleChange(StyleChangeKind.Class, element, className));
        }
    }

    /// <summary>Records a state change, which the updater can narrow.</summary>
    internal void InvalidateState(StyleNodeId element) {
        dirty = true;

        if (!cold) {
            changes.Add(new StyleChange(StyleChangeKind.State, element, null));
        }
    }

    /// <summary>Marks the document as needing a pass that changes no computed style.</summary>
    /// <remarks>
    ///     ⚠ <b>A scroll is the case this exists for, and it used to cost a full cascade.</b> An offset
    ///     moves where boxes are drawn and cannot change what any selector matches, so there is nothing
    ///     for the cascade to do — but the only way to ask for a pass was <see cref="Invalidate" />,
    ///     which meant every frame of a scroll re-resolved the document. <c>OnOffsetChanged</c>'s own
    ///     remarks said a scroll was "two walks of the tree and no work" and were describing
    ///     <c>Apply</c>, which is downstream of the pass they were counting.
    /// </remarks>
    internal void InvalidatePositions() => dirty = true;

    /// <summary>Runs the restyle, incrementally where the record allows it.</summary>
    /// <returns>How many elements were cascaded.</returns>
    int Restyle() {
        if (cold) {
            Restyler.ResolveAll();
            cold = false;
            changes.Clear();
            LastPassWasCold = true;
            return Restyler.LastPassResolved;
        }

        LastPassWasCold = false;
        var resolved = 0;

        // ⚠ **Replayed one at a time, and each one starts its own pass.** Two changes are not one
        // change with two subjects: the invalidator answers "what could *this* have reached", and
        // the sharing cache has to be cleared between them because an entry cached while resolving
        // the first knows nothing about the second having happened. Both are `StyleUpdater`'s
        // business and it does them per call, so this loop is the whole of the composition.
        //
        // Correct because the tree is already fully mutated before any of them run: every pass
        // resolves against the final state, so the union of what they reach is what a cold pass
        // would have produced. Replaying them against a tree being mutated in step would not be.
        foreach (var change in changes) {
            resolved += change.Kind == StyleChangeKind.Class
                ? Restyler.ClassChanged(change.Element, change.Name!)
                : Restyler.StateChanged(change.Element);
        }

        changes.Clear();
        return resolved;
    }

    /// <summary>Throws away the record, so the next pass resolves everything.</summary>
    /// <remarks>
    ///     ⚠ Called by <see cref="CompactStyles" /> as well as by <see cref="Invalidate" />, and the
    ///     compaction case is the one that is not obvious: a recorded change names a
    ///     <see cref="StyleNodeId" />, compaction moves every slot, and a change replayed afterwards
    ///     would restyle whatever has since landed on that index. Removal always invalidates, so
    ///     today the record is empty by the time compaction can run — this is what stops that from
    ///     being load-bearing.
    /// </remarks>
    void ForgetChanges() {
        cold = true;
        changes.Clear();
    }
}
