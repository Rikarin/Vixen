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
    StyleInvalidator invalidator;

    /// <summary>Which selector table <see cref="invalidator" /> was built over.</summary>
    SelectorTable invalidatorTable;

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
        invalidatorTable = engine.Selectors;
        invalidator = new StyleInvalidator(invalidatorTable);
    }

    /// <summary>Rebuilds the invalidator if the engine has replaced the tables it reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A latent crash that predates this method and was reachable through
    ///         <c>StyleEngine.Replace</c>.</b> The invalidator is built over
    ///         <see cref="StyleEngine.Selectors" /> and keeps a cursor into
    ///         <see cref="StyleEngine.Rules" />, and <c>StyleEngine.Reload</c> replaces both — so an
    ///         updater that held on to one was reading new rules' selector indices against the
    ///         previous stylesheet's table. A reload that produced <i>fewer</i> selectors read
    ///         somebody else's compound and quietly invalidated the wrong subtree; one that produced
    ///         more read off the end and threw.
    ///     </para>
    ///     <para>
    ///         It stayed invisible because the only reload in the engine was a hot edit of a sheet
    ///         that already existed, whose rule count barely moves. What made it reachable was
    ///         <c>@media</c> re-evaluation: a window crossing a breakpoint turns a block that was
    ///         dropped at load into rules, which is a reload that adds selectors by construction.
    ///     </para>
    ///     <para>
    ///         Compared by reference rather than tracked by a revision counter, because the tables
    ///         being new objects <i>is</i> the event — <c>StyleEngine.Build</c> replaces them together
    ///         and there is nothing else to observe.
    ///     </para>
    /// </remarks>
    void Refresh() {
        if (ReferenceEquals(invalidatorTable, engine.Selectors)) {
            return;
        }

        invalidatorTable = engine.Selectors;
        invalidator = new StyleInvalidator(invalidatorTable);
    }

    /// <summary>How many elements were cascaded by the last pass.</summary>
    /// <remarks>
    ///     The number the invalidation-minimality gate is about. Not how many elements were
    ///     <i>visited</i> — visiting is a pointer chase and cascading is the expensive thing.
    /// </remarks>
    public int LastPassResolved { get; private set; }

    /// <summary>How many elements the last pass visited without needing to descend past them.</summary>
    public int LastPassStopped { get; private set; }

    /// <summary>The time in seconds a started transition is stamped with.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A property rather than an argument on every entry point, because the clock is a
    ///         property of the <i>frame</i> and a pass is not the only thing in one.</b>
    ///         <see cref="ClassChanged" />, <see cref="StateChanged" /> and <see cref="ResolveAll" />
    ///         all replace styles and all happen inside one tick, so threading the same number through
    ///         three signatures would be three chances for two of them to disagree about when "now"
    ///         is — and a transition stamped with the wrong start time is a fade that begins in the
    ///         past and is half over on its first frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Left at zero is a working state and not a broken one, and that is the case worth
    ///         stating.</b> A host that never advances it resolves every style at <c>t = 0</c>, so
    ///         every transition it starts has zero elapsed time for ever and every animated property
    ///         sticks at the value it was leaving — an interface frozen at the previous frame's
    ///         appearance rather than one that jumps. <c>UiDocument</c> writes it from
    ///         <c>UiDocument.Tick</c>, which a host must call every frame regardless; see the remarks
    ///         there.
    ///     </para>
    /// </remarks>
    public float Now { get; set; }

    /// <summary>An element's current computed style.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its style, or <see cref="ComputedStyle.Empty" /> if it has not been resolved.</returns>
    public ComputedStyle StyleOf(StyleNodeId element) =>
        (uint) element.Index < (uint) styles.Length ? styles[element.Index] ?? ComputedStyle.Empty : ComputedStyle.Empty;

    /// <summary>Resolves every element from scratch.</summary>
    /// <remarks>
    ///     <para>
    ///         What a stylesheet reload does, and what an incremental pass is checked against.
    ///         Parents before children, because inheritance reads the parent's resolved table — and
    ///         elements are created parents-first, so ascending index already is that order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is the invariant removal has to protect</b>, and why a removed slot is
    ///         tombstoned rather than handed back out. See <see cref="StyleTree.Remove" />.
    ///     </para>
    /// </remarks>
    public void ResolveAll() {
        Refresh();
        invalidator.Read(engine.Rules);
        Grow();

        engine.Resolver.BeginPass();
        LastPassResolved = engine.Tree.LiveCount;
        LastPassStopped = 0;

        for (var i = 0; i < engine.Tree.Count; i++) {
            // A removed slot resolves to nothing rather than to a style. Left in, it would be
            // cascaded every pass, counted in the work the incremental story is measured by, and
            // put in the sharing cache under a key describing a tree it is no longer part of.
            var before = styles[i];
            styles[i] = engine.Tree.IsAliveAt(i) ? Resolve(i) : null;

            if (styles[i] is { } after) {
                Announce(i, before, after);
            }
        }
    }

    /// <summary>Shows the animator what a resolve replaced, so it can start what the change owes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Here rather than in the caller, because this is the only place both halves
    ///         exist.</b> A transition is started by a comparison — CSS has no event for "a class was
    ///         added" and does not need one — and the previous computed style is overwritten by the
    ///         line above. Anything downstream sees only the result and could not tell a fade from an
    ///         instant change if it wanted to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both passes, and the cold one is not the redundant case.</b> A cold pass is what
    ///         <c>UiDocument.Invalidate</c> costs, which is what a new element, a removal, an inline
    ///         style and a reparent all cost — so a transition that only the incremental path started
    ///         would be one that stopped working the moment anything else on the frame added an
    ///         element. The first cold pass of a document passes <c>null</c> and starts nothing, which
    ///         is CSS's rule: appear as specified, transition only on change.
    ///     </para>
    /// </remarks>
    void Announce(int index, ComputedStyle? before, ComputedStyle after) =>
        engine.Animations.Observe(new StyleNodeId(index), before, after, Now);

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
        Refresh();
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
            Announce(index, before, after);

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

    /// <summary>Moves the resolved styles to follow a compacted tree.</summary>
    /// <param name="remap">The mapping <see cref="StyleTree.Compact" /> produced.</param>
    /// <remarks>
    ///     ⚠ <b>Called after the tree is compacted and before anything reads a style.</b> These are
    ///     indexed by slot, so a compaction nothing told them about leaves every element wearing the
    ///     style of whatever used to be several slots along — an interface that is entirely wrong and
    ///     entirely plausible, since every style in it is a style some element really has.
    /// </remarks>
    public void Compact(ReadOnlySpan<int> remap) {
        // Forward, because a slot only ever moves down. Writing `to` cannot clobber a source not yet
        // read, which is what lets this happen in place.
        for (var i = 0; i < remap.Length && i < styles.Length; i++) {
            var to = remap[i];

            if (to >= 0) {
                styles[to] = styles[i];
            }
        }

        var live = 0;

        foreach (var to in remap) {
            live = Math.Max(live, to + 1);
        }

        // ⚠ Clamped to what has actually been resolved, because a compaction can happen before the
        // first pass does. `styles` is grown by `ResolveAll` and `Update` and by nothing else, so a
        // document that removed a subtree and compacted before it ever resolved has a full remap and
        // an empty array — and the clear below then ran with a negative length and threw. Slots past
        // the end hold no style to move and none to forget.
        live = Math.Min(live, styles.Length);

        // The tail is cleared rather than left. A stale style past the end would be returned by
        // `StyleOf` for a slot the tree has not handed out yet, which reads as an element that was
        // styled before it existed.
        Array.Clear(styles, live, styles.Length - live);
    }

    /// <remarks>
    ///     ⚠ <b>The inline block is part of a resolve and was missing here for two phases.</b>
    ///     <c>StyleEngine.ResolveAll</c> passes <c>Tree.InlineAt</c> and this did not, so every
    ///     declaration written on an element — a splitter at 37 %, a virtualised row at y = 880 000 —
    ///     was silently dropped by the incremental path. Nothing caught it because nothing called the
    ///     incremental path: inline styles arrived in Phase 4e, this class arrived in 4b, and the only
    ///     code between them was its own tests. <b>Dead code does not stay correct</b>, which is a
    ///     better argument for wiring it up than the frame cost was.
    /// </remarks>
    ComputedStyle Resolve(int index) {
        var parent = engine.Tree.ParentOf(index);
        return engine.Resolver.Resolve(
            engine.Tree,
            new StyleNodeId(index),
            parent < 0 ? null : styles[parent] ?? ComputedStyle.Empty,
            engine.Tree.InlineAt(index)
        );
    }

    void Grow() {
        if (styles.Length < engine.Tree.Count) {
            Array.Resize(ref styles, Math.Max(engine.Tree.Count, styles.Length * 2));
        }
    }
}
