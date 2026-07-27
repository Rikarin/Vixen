// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Holds every element's computed style, and keeps it that way as the tree changes.</summary>
/// <remarks>
///     <para>
///         Two passes and they are the same code. A cold pass resolves every element; an incremental
///         one resolves the elements a change could have reached and then keeps going only where the
///         answer actually moved. The second is a subset of the first, which is the property the
///         gate checks.
///     </para>
///     <para>
///         The stopping rule is the whole design. Re-resolving an element gives back an
///         <i>interned</i> <see cref="ComputedStyle" />, so "did anything change" is
///         <see cref="object.ReferenceEquals(object, object)" /> — and if it did not, no descendant
///         can have changed by inheritance either, so the walk stops. That is what makes
///         [doc 09](../../docs/plan/09-ui-framework.md)'s reference comparison load-bearing rather
///         than a nicety: without it, every invalidation would have to assume the worst and descend
///         to the leaves.
///     </para>
/// </remarks>
public sealed class StyleUpdater {
    readonly StyleEngine engine;
    readonly StyleInvalidator invalidator;
    readonly List<StyleNodeId> roots = [];
    readonly HashSet<int> queued = [];

    // Ordered by element index rather than FIFO, and it has to be. Parents always have a lower
    // index than their children, so ascending index *is* parents-before-children — and a queue
    // would happily hand back a deep invalidation root before the ancestor that it inherits from,
    // which would then inherit from a style about to be replaced.
    readonly PriorityQueue<int, int> pending = new();
    ComputedStyle?[] styles = [];

    /// <summary>Creates an updater over an engine.</summary>
    /// <param name="engine">The engine.</param>
    public StyleUpdater(StyleEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        invalidator = new StyleInvalidator(engine.Selectors);
    }

    /// <summary>How many elements were cascaded by the last pass.</summary>
    /// <remarks>
    ///     The number the invalidation-minimality gate is about. Not how many elements were
    ///     <i>visited</i> — visiting is a pointer chase and cascading is the expensive thing.
    /// </remarks>
    public int LastPassResolved { get; private set; }

    /// <summary>How many elements the last pass visited without needing to descend past them.</summary>
    public int LastPassStopped { get; private set; }

    /// <summary>An element's current computed style.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its style, or <see cref="ComputedStyle.Empty" /> if it has not been resolved.</returns>
    public ComputedStyle StyleOf(StyleNodeId element) =>
        (uint) element.Index < (uint) styles.Length ? styles[element.Index] ?? ComputedStyle.Empty : ComputedStyle.Empty;

    /// <summary>Resolves every element from scratch.</summary>
    /// <remarks>
    ///     What a stylesheet reload does, and what an incremental pass is checked against. Parents
    ///     before children, because inheritance reads the parent's resolved table — and elements are
    ///     created parents-first, so ascending index already is that order.
    /// </remarks>
    public void ResolveAll() {
        invalidator.Read(engine.Rules);
        Grow();

        engine.Resolver.BeginPass();
        LastPassResolved = engine.Tree.Count;
        LastPassStopped = 0;

        for (var i = 0; i < engine.Tree.Count; i++) {
            styles[i] = Resolve(i);
        }
    }

    /// <summary>Restyles what a change to an element's classes could have reached.</summary>
    /// <param name="element">The element that changed.</param>
    /// <param name="changedClasses">The classes added or removed.</param>
    /// <returns>How many elements had to be cascaded.</returns>
    public int ClassChanged(StyleNodeId element, params ReadOnlySpan<string> changedClasses) {
        var names = new int[changedClasses.Length];
        for (var i = 0; i < changedClasses.Length; i++) {
            names[i] = engine.Names.Lookup(changedClasses[i]);
        }

        return Update(element, names, stateChanged: false);
    }

    /// <summary>Restyles what a change to an element's pseudo state could have reached.</summary>
    /// <param name="element">The element that changed.</param>
    /// <returns>How many elements had to be cascaded.</returns>
    public int StateChanged(StyleNodeId element) => Update(element, [], stateChanged: true);

    int Update(StyleNodeId element, ReadOnlySpan<int> changed, bool stateChanged) {
        invalidator.Read(engine.Rules);
        Grow();

        // The sharing cache is scoped to a pass, and this is why. Its key describes what an element
        // *is* — parent, tag, classes, state — and an entry cached before the change knows nothing
        // about the parent having gained a class since. Within one pass the tree is fixed and the
        // key is sound; across passes it is stale, and clearing is both correct and cheaper than
        // working out which entries to evict.
        engine.Resolver.BeginPass();
        LastPassResolved = 0;
        LastPassStopped = 0;

        invalidator.Collect(engine.Tree, element, changed, stateChanged, roots);

        pending.Clear();
        queued.Clear();

        foreach (var root in roots) {
            if (queued.Add(root.Index)) {
                pending.Enqueue(root.Index, root.Index);
            }
        }

        while (pending.Count > 0) {
            var index = pending.Dequeue();
            var before = styles[index];
            var after = Resolve(index);

            styles[index] = after;
            LastPassResolved++;

            // Not "did anything change" but "did anything a child would have inherited change".
            // The coarser test is what made selecting one row of a grid restyle its hundred cells:
            // a highlight that sets `background` changes the row's style and cannot possibly reach
            // a cell, and descending on it undoes the whole point. Any descendant invalidated in its
            // own right is already in the queue and unaffected by stopping here.
            if (before is not null && !engine.Resolver.Inherited.InheritedPortionDiffers(before, after)) {
                LastPassStopped++;
                continue;
            }

            var owner = new StyleNodeId(index);
            for (var i = 0; i < engine.Tree.GetChildCount(owner); i++) {
                var child = engine.Tree.GetChild(owner, i);
                if (queued.Add(child.Index)) {
                    pending.Enqueue(child.Index, child.Index);
                }
            }
        }

        return LastPassResolved;
    }

    ComputedStyle Resolve(int index) {
        var parent = engine.Tree.ParentOf(index);
        return engine.Resolver.Resolve(
            engine.Tree,
            new StyleNodeId(index),
            parent < 0 ? null : styles[parent] ?? ComputedStyle.Empty
        );
    }

    void Grow() {
        if (styles.Length < engine.Tree.Count) {
            Array.Resize(ref styles, Math.Max(engine.Tree.Count, styles.Length * 2));
        }
    }
}
