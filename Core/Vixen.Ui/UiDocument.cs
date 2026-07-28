// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui.Layout;
using Vixen.Ui.Styling;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>An element tree, its stylesheets, and the pass that turns one into geometry.</summary>
/// <remarks>
///     <para>
///         Three subsystems that were built and tested apart finally run together here: the cascade
///         decides what applies, <see cref="LayoutStyleBuilder" /> turns that into lengths, and the
///         flexbox engine turns those into rectangles. Everything before this point could be judged
///         by a conformance suite; this is the first thing that can be judged by looking at it.
///     </para>
///     <para>
///         <b>The pass is four walks and they cannot be merged.</b> The cascade needs parents
///         resolved before children because inheritance reads the parent's resolved table; font size
///         needs the same order for the same reason and cannot be folded into the cascade because it
///         is a <i>computed</i> value the cascade has no opinion about; the layout style depends on
///         the font size; and layout itself is the flexbox algorithm, which is not a walk at all.
///     </para>
///     <para>
///         Elements can be removed as well as added — see <see cref="Remove" /> — but a removed
///         style slot is tombstoned rather than reused, so a document that builds and tears down a
///         list every frame grows without bound. <see cref="StyleTree.DeadCount" /> is the number
///         that says so, and compaction is owed.
///     </para>
/// </remarks>
public sealed partial class UiDocument : IDisposable {
    readonly DrawListBuilder drawings;
    readonly int pointerEvents;
    readonly int fontFamily;
    readonly int fontWeight;
    readonly int fontStyle;
    readonly int fontStretch;
    readonly int overflow;
    /// <summary>How many tombstoned slots it takes before compacting is worth the walk.</summary>
    /// <remarks>
    ///     A floor rather than a pure ratio, because the ratio alone would compact a four-element
    ///     document that removed three — a walk of the whole tree to reclaim three slots, on the frame
    ///     where somebody happened to close a menu.
    /// </remarks>
    const int CompactionFloor = 64;

    readonly int none;
    readonly int visible;

    /// <summary>The subtrees a <c>Remove</c> is part-way through announcing.</summary>
    /// <remarks>
    ///     ⚠ <b>Because <c>OnRemoved</c> is allowed to remove things and one of them would corrupt
    ///     the walk.</b> Removing a popup from inside the hook is the whole point and is safe. Removing
    ///     an <i>ancestor</i> of the subtree currently being announced is not: the outer call is
    ///     holding an element it is about to detach, and the inner one would detach it first, leaving
    ///     the outer one to take a node out of a parent it no longer has. Refused with a message
    ///     rather than left to be found as a null reference three frames later.
    /// </remarks>
    readonly List<UiElement> removing = [];

    bool dirty = true;

    /// <summary>Creates a document over a surface of a given size.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rootFontSize">The font size <c>rem</c> measures against.</param>
    public UiDocument(float width, float height, float rootFontSize = LengthContext.InitialFontSize) {
        Styles = new StyleEngine();
        Restyler = new StyleUpdater(Styles);
        Layout = new LayoutTree();
        Builder = new LayoutStyleBuilder(Styles.Properties, Styles.Values, Styles.Names);
        drawings = new DrawListBuilder(Styles.Properties, Styles.Values, Styles.Names);
        Viewport = LengthContext.ForViewport(width, height, rootFontSize);

        reader = new StyleValueParser(Styles.Values, Styles.Names);

        pointerEvents = Styles.Properties.Intern("pointer-events");
        color = Styles.Properties.Intern("color");
        fontFamily = Styles.Properties.Intern("font-family");
        fontWeight = Styles.Properties.Intern("font-weight");
        fontStyle = Styles.Properties.Intern("font-style");
        fontStretch = Styles.Properties.Intern("font-stretch");
        overflow = Styles.Properties.Intern("overflow");
        none = Styles.Values.Intern("none");
        visible = Styles.Values.Intern("visible");

        Root = Create("root", null, null, []);
    }

    /// <summary>The cascade.</summary>
    public StyleEngine Styles { get; }

    /// <summary>What holds every element's computed style and keeps it that way.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>StyleEngine.ResolveAll</c>, which is what this used to be and is why a hover
    ///     cost a full cascade.</b> The engine resolves the document; the updater resolves what a
    ///     change could have reached and stops descending where the answer did not move. Both produce
    ///     the same styles — that is the property <c>IncrementalDocumentTests</c> gates — and only one
    ///     of them is affordable sixty times a second.
    /// </remarks>
    public StyleUpdater Restyler { get; }

    /// <summary>The flexbox engine.</summary>
    public LayoutTree Layout { get; }

    /// <summary>The step between them.</summary>
    public LayoutStyleBuilder Builder { get; }

    /// <summary>The commands the last <see cref="Draw" /> produced.</summary>
    public DrawList Drawing { get; } = new();

    /// <summary>The surface's size and root font size.</summary>
    public LengthContext Viewport { get; private set; }

    /// <summary>The element every other one descends from.</summary>
    public UiElement Root { get; }

    /// <summary>How many elements had a layout style written on the last pass.</summary>
    /// <remarks>
    ///     Exposed because it is the number the incremental story is about, and a claim about work
    ///     avoided that cannot be measured is a claim nobody can check. A second
    ///     <see cref="Update" /> over an unchanged tree should report zero.
    /// </remarks>
    public int StylesApplied { get; private set; }

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <returns>The sheet's index, for <see cref="ReloadStyles" />.</returns>
    public int Load(string css, StyleOrigin origin = StyleOrigin.Author) {
        var sheet = Styles.Load(css, origin);
        Invalidate();
        return sheet;
    }

    /// <summary>Replaces a loaded stylesheet with new text.</summary>
    /// <param name="sheet">The index <see cref="Load" /> returned.</param>
    /// <param name="css">The new text.</param>
    /// <remarks>
    ///     <para>
    ///         Forgets what every element applied, for the same reason <see cref="Resize" /> does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is currently redundant, and kept anyway.</b> A reload rebuilds the interning
    ///         cache, so a computed style from before it is a different object from the identical
    ///         one after — the pass's reference comparison already calls every element changed, and
    ///         replacing this with a plain <c>Invalidate</c> breaks no test. It stays because the
    ///         redundancy is an accident of how the reload happens to be implemented rather than a
    ///         property of what it means, and an interning cache that survived a reload one day
    ///         would turn that accident into every element keeping the geometry a deleted rule gave
    ///         it. Said out loud rather than defended by a test that cannot exist.
    ///     </para>
    /// </remarks>
    public void ReloadStyles(int sheet, string css) {
        Styles.Replace(sheet, css);
        Forget();
    }

    /// <summary>Changes the surface's size.</summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">The new height.</param>
    /// <remarks>
    ///     ⚠ Forgets what was applied rather than only marking the document dirty. Nothing an
    ///     element <i>declared</i> has changed, so its computed style is the same interned object
    ///     and its font size is the same number — the skip below would match on both and every
    ///     <c>vw</c> in the document would keep its old value while the window visibly changed size.
    ///     A document with no viewport-relative length pays for the rebuild, and finding out which
    ///     documents those are is not worth the bookkeeping: resizing happens at human speed.
    /// </remarks>
    public void Resize(float width, float height) {
        Viewport = Viewport with { ViewportWidth = width, ViewportHeight = height };
        Forget();
    }

    /// <summary>Marks the document as needing a fresh pass over every element.</summary>
    /// <remarks>
    ///     ⚠ <b>The conservative door, and every caller that is not a class or a state change comes
    ///     through it.</b> A new element, a removal, a move, an inline style and a stylesheet all land
    ///     here and all cost a cold pass. That is correct — <see cref="StyleUpdater" /> narrows a
    ///     change to <i>an existing element's</i> names or state and cannot express any of them — and
    ///     it is the reason this stays public and unnarrowed: an outside caller that has changed
    ///     something the document cannot see must get the pass that assumes the worst.
    /// </remarks>
    public void Invalidate() {
        dirty = true;
        ForgetChanges();
    }

    /// <summary>Marks every element as needing its layout style rebuilt.</summary>
    void Forget() {
        Forget(Root);
        Invalidate();
    }

    static void Forget(UiElement element) {
        element.AppliedStyle = null;

        foreach (var child in element.Children) {
            Forget(child);
        }
    }

    /// <summary>Creates an element.</summary>
    /// <param name="tag">Its element name.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    public UiElement Create(string tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames) =>
        Create<UiElement>(tag, parent, id, classNames);

    /// <summary>Creates an element of a particular type.</summary>
    /// <typeparam name="T">The element type, which needs a parameterless constructor.</typeparam>
    /// <param name="tag">Its element name, or <c>null</c> to take the one the type answers to.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instance is made before the style node, which is the opposite of the obvious
    ///         order and is what lets a type name itself.</b> A control's stylesheet selects on its
    ///         tag — <c>button { … }</c> — and a caller that had to pass <c>"button"</c> alongside
    ///         <c>Button</c> would eventually pass something else, at which point the control is
    ///         still a <see cref="UiElement" /> and silently unstyled. Asking the element for
    ///         <see cref="UiElement.TagName" /> makes the two impossible to disagree.
    ///     </para>
    ///     <para>
    ///         <b>Three steps, in this order:</b> bind, attach, then
    ///         <see cref="UiElement.OnCreated" />. A control builds its parts in that last one and
    ///         every one of them needs a document to be created in — so the hook cannot be a
    ///         constructor, and it cannot run before the element is in the tree either, because a
    ///         part added to an unattached parent would be laid out relative to nothing.
    ///     </para>
    /// </remarks>
    public T Create<T>(string? tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames)
        where T : UiElement, new() {
        var element = new T();
        tag ??= element.TagName;

        var styleNode = Styles.Tree.CreateElement(tag, parent?.StyleNode, id, classNames);
        var layoutNode = Layout.CreateNode();

        element.Bind(this, tag, parent, styleNode, layoutNode);

        if (parent is not null) {
            parent.Attach(element);
            Layout.AddChild(parent.LayoutNode, layoutNode);
        }

        Invalidate();
        element.OnCreated();

        return element;
    }

    /// <summary>Moves an element to another position among its siblings.</summary>
    /// <param name="element">The element to move.</param>
    /// <param name="index">Where it should end up.</param>
    /// <remarks>
    ///     <para>
    ///         All three stores at once, for the same reason removal is: an element is a handle into
    ///         a style tree and a layout tree, and one moved in only two of them is in two places.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reordering is a style change, not just a layout one.</b> <c>:nth-child</c>,
    ///         <c>:first-child</c> and the sibling combinators all read position, so moving an
    ///         element restyles it and the siblings it passed. That is why this invalidates rather
    ///         than only marking the layout dirty — and it is the reason a reconciler that moves
    ///         elements is worth having over one that rebuilds them, because a rebuild loses the
    ///         focus and the scroll position as well.
    ///     </para>
    ///     <para>
    ///         Within one parent only. Reparenting would move an element's style slot relative to
    ///         its new parent's, and a child whose slot is below its parent's breaks the three
    ///         passes that read slot order as depth order — the same invariant that makes removal
    ///         tombstone rather than reuse.
    ///     </para>
    /// </remarks>
    public void Move(UiElement element, int index) {
        ArgumentNullException.ThrowIfNull(element);

        if (!ReferenceEquals(element.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(element));
        }

        if (element.Parent is not { } parent) {
            throw new InvalidOperationException("the root has no siblings to move among.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, parent.Children.Count);

        if (element.IndexInParent == index) {
            return;
        }

        parent.MoveChild(element, index);
        Layout.RemoveChild(parent.LayoutNode, element.LayoutNode);
        Layout.InsertChild(parent.LayoutNode, element.LayoutNode, index);
        Styles.Tree.Move(element.StyleNode, index);
        Invalidate();
    }

    /// <summary>Removes an element and everything under it.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>
    ///     <para>
    ///         Out of all three stores at once, which is the point of doing it here rather than in
    ///         any of them: an element is a handle into a style tree and a layout tree, and one that
    ///         left either behind would keep matching selectors or keep taking up space in a flex
    ///         line while being gone from the document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whatever was pointing at it has to stop.</b> The focus, a captured pointer and a
    ///         gesture in progress all name an element, and each of them outlives the element unless
    ///         something says otherwise — a drag whose target was deleted mid-drag delivers its next
    ///         move to a detached object, and a focus left on a removed element makes Tab start from
    ///         somewhere that is not on the screen.
    ///     </para>
    ///     <para>
    ///         The root cannot be removed. A document without one has no tree to walk and nothing to
    ///         lay out, and the alternative to refusing is a null check in every pass.
    ///     </para>
    /// </remarks>
    public void Remove(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        if (ReferenceEquals(element, Root)) {
            throw new InvalidOperationException("the root cannot be removed — a document is its tree.");
        }

        // ⚠ Before the ownership check, which reads `Document` — and a removed element throws on
        // that rather than answering. Two controls may name the same popup, and the second one to go
        // should find it already gone rather than be told it belongs to nobody.
        if (element.IsRemoved) {
            return;
        }

        if (!ReferenceEquals(element.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(element));
        }

        // An `OnRemoved` that removes something already on its way out. Its own subtree is fine — it
        // is about to go regardless — but an ancestor of one is not: see `removing`.
        foreach (var pending in removing) {
            for (var ancestor = pending; ancestor is not null; ancestor = ancestor.Parent) {
                if (ReferenceEquals(ancestor, element)) {
                    throw new InvalidOperationException(
                        "OnRemoved cannot remove an ancestor of the element being removed — "
                        + "the outer removal is holding it. Remove what the control owns elsewhere in "
                        + "the tree instead."
                    );
                }
            }
        }

        // ⚠ Before anything is detached, and before `Release`, because an override's whole purpose is
        // to reach elsewhere in the document — a menu closing the popover it parented on the root —
        // and a handler that runs after the subtree is out of the stores can ask almost nothing. It
        // may remove other elements; `removing` is what stops it removing one of these.
        removing.Add(element);

        try {
            Announce(element);
        } finally {
            removing.Remove(element);
        }

        // Before anything is detached, because finding out whether the focus is inside the subtree
        // means walking up from the focus to a parent this is about to clear.
        Release(element);

        element.Parent?.Detach(element);
        Layout.RemoveChild(element.Parent!.LayoutNode, element.LayoutNode);
        Layout.DestroyRecursive(element.LayoutNode);
        Styles.Tree.Remove(element.StyleNode);

        Retire(element);
        Invalidate();
    }

    /// <summary>Drops anything that was pointing into a subtree about to go.</summary>
    void Release(UiElement element) {
        for (var focused = Focused; focused is not null; focused = focused.Parent) {
            if (ReferenceEquals(focused, element)) {
                Focus(null);
                break;
            }
        }

        for (var captured = Captured; captured is not null; captured = captured.Parent) {
            if (ReferenceEquals(captured, element)) {
                ReleasePointer();
                break;
            }
        }

        Gestures.Forget(element);
        ForgetHover(element);
    }

    /// <summary>Tells a subtree it is going, deepest last.</summary>
    /// <remarks>
    ///     ⚠ <b>Parents before children, which is the opposite of a disposal order and is right
    ///     here.</b> A control's <c>OnRemoved</c> tears down what it owns, and what it owns includes
    ///     its own parts — so a panel that closes its menu wants to run before that menu's own hook,
    ///     not after it has already been told. It mirrors <c>OnCreated</c>, which builds outward from
    ///     the type that was asked for.
    ///
    ///     The list is snapshotted per level, because a handler may add or remove children of the
    ///     element it is called on — a popover closing removes its own items — and iterating the live
    ///     collection would then skip half of them.
    /// </remarks>
    static void Announce(UiElement element) {
        element.OnRemoved();

        foreach (var child in element.Children.ToArray()) {
            Announce(child);
        }
    }

    /// <summary>Marks a subtree as no longer part of any document.</summary>
    static void Retire(UiElement element) {
        element.Retire();

        foreach (var child in element.Children) {
            Retire(child);
        }
    }

    /// <summary>How many times the style store has been compacted.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>DrawList.Batched</c> is: "a document that builds and tears
    ///     down a list no longer grows without bound" is a claim, and a claim about work that cannot
    ///     be counted is one nobody can check.
    /// </remarks>
    public int StyleCompactions { get; private set; }

    /// <summary>Reclaims the style slots removal left behind.</summary>
    /// <returns>Whether anything was reclaimed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the document's to do and nobody else's.</b> A slot is an index, so
    ///         compacting moves every <c>StyleNodeId</c> in existence — and the only object that
    ///         knows where they all are is the one that handed them out. <c>StyleTree.Compact</c>
    ///         therefore returns a mapping rather than doing this quietly, and this is what walks the
    ///         element tree applying it.
    ///     </para>
    ///     <para>
    ///         Public as well as automatic, because a caller that has just torn down a large subtree
    ///         knows something the heuristic below does not.
    ///     </para>
    /// </remarks>
    public bool CompactStyles() {
        var tree = Styles.Tree;

        if (tree.DeadCount == 0) {
            return false;
        }

        var remap = new int[tree.Count];
        tree.Compact(remap);
        Remap(Root, remap);

        // The updater's styles are indexed by slot, so a compaction it was not told about would leave
        // every element wearing the style of whatever used to be several slots along.
        //
        // ⚠ **Insurance, and labelled as insurance because a sabotage deleting it failed to fail.**
        // The line below forces the next pass to be cold, and a cold pass writes every entry of that
        // array — so the remapped values are overwritten before anything can read one. It is kept
        // because `StyleUpdater.Compact` is part of the updater's own contract rather than a
        // courtesy, and because the redundancy is a property of *these two lines being adjacent*: a
        // compaction that one day preserves the incremental pass makes the remap load-bearing again,
        // and finding that out by way of a wrong interface would be finding it out the hard way.
        Restyler.Compact(remap);

        // ⚠ This one is not insurance. A recorded change names a slot, compaction moves every slot,
        // and a change replayed afterwards would restyle whatever has since landed on that index.
        ForgetChanges();
        StyleCompactions++;

        return true;
    }

    /// <summary>Points every element at the slot its style moved to.</summary>
    /// <remarks>
    ///     ⚠ A walk of the tree, so every live element is reached exactly once and no removed one is.
    ///     A list in creation order would need the removed entries taken out of it first, which is the
    ///     bookkeeping compaction exists to stop doing.
    /// </remarks>
    static void Remap(UiElement element, ReadOnlySpan<int> remap) {
        element.Restyle(new StyleNodeId(remap[element.StyleNode.Index]));

        foreach (var child in element.Children) {
            Remap(child, remap);
        }
    }

    /// <summary>Runs the passes, if anything has changed since the last one.</summary>
    /// <returns>Whether any work was done.</returns>
    public bool Update() {
        if (!dirty) {
            StylesApplied = 0;
            return false;
        }

        dirty = false;
        StylesApplied = 0;
        StylesResolved = 0;

        // ⚠ Before anything reads a slot, and only when the tombstones outnumber the elements. Here
        // rather than in `Remove`, because compaction is O(elements) and removing a thousand-row list
        // one row at a time would then be O(elements²) — and because a pass is the one moment where
        // every id is about to be re-read anyway, so nothing is holding a stale one across it.
        //
        // The floor stops a document with four elements compacting because it removed three.
        if (Styles.Tree.DeadCount >= CompactionFloor && Styles.Tree.DeadCount > Styles.Tree.LiveCount) {
            CompactStyles();
        }

        StylesResolved = Restyle();
        Apply(Root, Viewport.RootFontSize);

        Layout.CalculateLayout(Root.LayoutNode, Viewport.ViewportWidth, Viewport.ViewportHeight, Direction.Ltr);
        Accumulate(Root, 0f, 0f);

        Settle();
        return true;
    }

    /// <summary>Raised when every box in the document is final for this frame.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a control needs and could not have.</b> A scroll bar's range is its content's
    ///         height, a virtualiser's row count is its viewport's, and both are results of the layout
    ///         rather than inputs to it — so a control that computed them in a property setter was
    ///         computing them against the previous frame's boxes. <c>ScrollView.Refresh</c>,
    ///         <c>TreeView.Refresh</c> and the sample's own resize handler all existed to paper over
    ///         that, and all of them are a caller being asked to know when the framework had finished.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A handler may change the document, and doing so is normal rather than an abuse.</b>
    ///         A virtualiser that has just learned its viewport is taller realises more rows, which is
    ///         a structural change to the tree during a pass that has already run. So this re-enters:
    ///         after the handlers, a document that was dirtied runs the whole pass again, and it keeps
    ///         going until nothing more is asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounded, because the fixed point is not guaranteed to exist.</b> A handler that
    ///         adds a row whenever it is called, or two that undo each other, would spin for ever — and
    ///         "the interface hangs" is a worse failure than any interface it could produce. After
    ///         <see cref="SettlePasses" /> attempts the loop stops and <see cref="Settled" /> reports
    ///         false, which is a frame drawn one pass stale rather than a frame never drawn.
    ///     </para>
    /// </remarks>
    public event Action<UiDocument>? LayoutFinished;

    /// <summary>How many times a pass will re-run for handlers that changed something.</summary>
    /// <remarks>
    ///     Three, because the shapes that legitimately need more than one are two deep — a virtualiser
    ///     inside a scroll view, where realising rows changes the content size, which changes the
    ///     bar's range, which can change the viewport's width — and nothing sane is three.
    /// </remarks>
    public const int SettlePasses = 3;

    /// <summary>Whether the last <see cref="Update" /> reached a fixed point.</summary>
    /// <remarks>
    ///     False means a handler was still asking for changes when the budget ran out, and the frame
    ///     is one pass behind what it asked for. Exposed rather than logged because a control that
    ///     does this is a bug in that control, and a number nobody can read is a bug nobody finds.
    /// </remarks>
    public bool Settled { get; private set; } = true;

    /// <summary>How many extra passes the last <see cref="Update" /> ran for its handlers.</summary>
    public int SettlingPasses { get; private set; }

    void Settle() {
        SettlingPasses = 0;
        Settled = true;

        if (LayoutFinished is null) {
            return;
        }

        for (var pass = 0; pass <= SettlePasses; pass++) {
            LayoutFinished.Invoke(this);

            if (!dirty) {
                return;
            }

            if (pass == SettlePasses) {
                Settled = false;
                return;
            }

            dirty = false;
            SettlingPasses++;

            StylesResolved += Restyle();
            Apply(Root, Viewport.RootFontSize);
            Layout.CalculateLayout(Root.LayoutNode, Viewport.ViewportWidth, Viewport.ViewportHeight, Direction.Ltr);
            Accumulate(Root, 0f, 0f);
        }
    }

    /// <summary>Lets time pass, for the things that happen because nothing happened.</summary>
    /// <param name="now">The host's clock.</param>
    /// <remarks>
    ///     ⚠ <b>Time arrives from the host rather than from a clock read here</b>, which is the same
    ///     decision <c>GestureRecognizer</c> made and for the same reasons: a framework that calls
    ///     <c>DateTime.Now</c> cannot be tested without sleeping, cannot replay a recorded trace, and
    ///     behaves differently when a breakpoint holds the frame.
    ///
    ///     A long press, a tooltip's delay and a toast's dismissal are all things that must happen
    ///     when <i>no</i> input arrives, and nothing in an input stream can report the absence of
    ///     input. This is the one call a host must make every frame whether anything happened or not.
    /// </remarks>
    public void Tick(TimeSpan now) {
        Now = now;
        Gestures.Tick(now);
        Ticked?.Invoke(this, now);
    }

    /// <summary>The last time <see cref="Tick" /> was given.</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>Raised on every <see cref="Tick" />.</summary>
    /// <remarks>
    ///     A control subscribes in <c>OnCreated</c> and unsubscribes in <c>OnRemoved</c> — which is
    ///     the second thing that hook turned out to be for, and a reminder that it was the missing
    ///     half of a pair rather than a convenience.
    /// </remarks>
    public event Action<UiDocument, TimeSpan>? Ticked;

    /// <summary>Writes each element's resolved style through to the layout store.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A walk of the tree rather than of a list in creation order</b>, which is what
    ///         removal forced and what should have been here anyway. The list version was correct
    ///         only because elements were created parents-first and never removed, so its index order
    ///         happened to be its depth order — an invariant a removal would quietly have broken,
    ///         with children resolved against a parent's font size from the previous frame. The
    ///         property this actually needs is "parents before children", and a descent is that by
    ///         construction rather than by coincidence.
    ///     </para>
    ///     <para>
    ///         It also deletes two arrays. What each element had applied last time is now on the
    ///         element, where removing one takes its bookkeeping with it instead of leaving a hole
    ///         in three parallel lists.
    ///     </para>
    /// </remarks>
    void Apply(UiElement element, float parentFontSize) =>
        Apply(element, parentFontSize, ComputedText.Initial);

    void Apply(UiElement element, float parentFontSize, in ComputedText parentText) {
        var style = Restyler.StyleOf(element.StyleNode);

        element.Style = style;
        element.FontSize = Builder.ResolveFontSize(style, parentFontSize, Viewport);

        // ⚠ After the font size and before the children, because these are relative to *this*
        // element's size and are inherited already absolute. That ordering is the whole of the
        // computed-value stage — see ComputedText.
        element.TextStyle = ResolveText(style, parentText, element.FontSize);

        // ⚠ Reference equality, which is the whole reason ComputedStyle is interned. Two elements
        // that resolved alike hold the same object, so this is one pointer comparison rather than a
        // walk of a property table — and a table of ten thousand identical cells rebuilds nothing.
        //
        // The font size has to be part of the test as well as the style: an element whose own
        // declarations did not change still needs rebuilding if an ancestor's font size did, because
        // every `em` on it measures against a different number now.
        if (!ReferenceEquals(element.AppliedStyle, style) || !element.AppliedFontSize.Equals(element.FontSize)) {
            element.AppliedStyle = style;
            element.AppliedFontSize = element.FontSize;
            StylesApplied++;

            Layout.SetStyle(element.LayoutNode, Builder.Build(style, Viewport.WithFontSize(element.FontSize)));
        }

        foreach (var child in element.Children) {
            Apply(child, element.FontSize, element.TextStyle);
        }
    }

    /// <summary>Rebuilds the draw list from the current layout and styles.</summary>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    /// <remarks>
    ///     Separate from <see cref="Update" /> because they answer different questions and a caller
    ///     may want one without the other — a hit test needs layout and no drawing, and a window
    ///     that was merely uncovered needs the drawing and no layout.
    /// </remarks>
    public bool Draw() => drawings.Build(this, Drawing);

    /// <summary>The element a pointer would land on.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The deepest element under the point, or <c>null</c> if none is.</returns>
    /// <remarks>
    ///     <para>
    ///         Front to back, which for children drawn in document order means <b>last child
    ///         first</b>. A later sibling is painted over an earlier one, so it is the one a click
    ///         lands on, and testing in document order would return whatever happens to be
    ///         underneath.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>pointer-events: none</c> makes an element transparent to the pointer <i>without
    ///         making its children so</i> — that asymmetry is what makes an overlay usable, and
    ///         treating the subtree as one unit would either block everything under a full-screen
    ///         layer or let clicks through a modal.
    ///     </para>
    ///     <para>
    ///         Doc 09 asks for a quadtree over the top level. This descends the tree instead, which
    ///         only enters subtrees that contain the point, so it is O(depth × siblings) rather than
    ///         O(elements). The quadtree is owed and should be measured against this before it is
    ///         written — the doc says "measured to be sufficient" about the simple version and that
    ///         measurement has not been taken.
    ///     </para>
    /// </remarks>
    public UiElement? HitTest(float x, float y) => HitTest(Root, x, y);

    /// <summary>Sends a pointer event to whatever is under it.</summary>
    /// <param name="args">The event, positioned in document space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    /// <remarks>
    ///     ⚠ A captured pointer goes to the capturing element wherever it is, which is the whole
    ///     point of capture: a drag that leaves the scrollbar it started on must keep reaching the
    ///     scrollbar. Hit testing during a drag is exactly the bug capture exists to prevent.
    /// </remarks>
    public UiElement? Dispatch(PointerEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        // Before the event rather than after it. `:hover` and `:active` are what a handler reads to
        // find out what it is being asked about — a menu deciding whether the release it just got
        // belongs to the item under the cursor asks the item — and state brought up to date
        // afterwards would answer every handler with the previous frame's arrangement.
        Track(args);

        var target = Captured ?? HitTest(args.X, args.Y);
        target?.Raise(args);

        // After the raw event rather than instead of it. A gesture is a reading of the pointer
        // stream, not a replacement for it, and a control that wants presses and a control that
        // wants taps are both entitled to what they asked for.
        Gestures.Process(args, target);
        return target;
    }

    /// <summary>Taps, long presses and drags read out of the pointer stream.</summary>
    /// <remarks>
    ///     Exposed rather than hidden behind the document because it needs telling what time it is —
    ///     see <see cref="GestureRecognizer.Tick" /> — and because its thresholds are an
    ///     application's decision.
    /// </remarks>
    public GestureRecognizer Gestures { get; } = new();

    /// <summary>The element currently receiving every pointer event, if any.</summary>
    public UiElement? Captured { get; private set; }

    /// <summary>Sends every pointer event to one element until it is released.</summary>
    /// <param name="element">The element.</param>
    public void CapturePointer(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        Captured = element;
    }

    /// <summary>Stops sending every pointer event to one element.</summary>
    public void ReleasePointer() => Captured = null;

    /// <summary>The faces a <c>font-family</c> declaration can name.</summary>
    public FontRegistry Fonts { get; } = new();

    /// <summary>The shaping every element's text goes through.</summary>
    /// <remarks>
    ///     Shared across the document because it is keyed on the font and the string and not on the
    ///     element — ten thousand list rows saying the same word shape once between them, and the
    ///     measure pass and the draw pass shape once between them too.
    /// </remarks>
    public ShapingCache Shaping { get; } = new();

    internal bool PointerEventsNone(ComputedStyle style) =>
        style.TryGet(pointerEvents, out var value) && value == none;

    internal string? FontFamilyOf(ComputedStyle style) =>
        style.TryGet(fontFamily, out var value) ? Styles.Values.NameOf(value) : null;

    /// <summary>What <c>font-weight</c>, <c>font-style</c> and <c>font-stretch</c> asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>bold</c> and <c>normal</c> are keywords and <c>700</c> is a number, and the
    ///         cascade hands both over as interned names.</b> So this reads the name and parses it,
    ///         rather than asking for a number and getting nothing whenever an author wrote the
    ///         keyword — which is how almost everybody writes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>bolder</c> and <c>lighter</c> are not supported and are read as <c>normal</c>.</b>
    ///         They are relative to the <i>parent's computed</i> weight, so they need the same
    ///         computed-value stage <see cref="ComputedText" /> is — one more inherited value carried
    ///         down resolved. Recorded rather than approximated, because approximating them means
    ///         picking a weight nobody asked for.
    ///     </para>
    /// </remarks>
    internal FontQuery FontQueryOf(ComputedStyle style) {
        var weight = 400;
        var slant = FontStyle.Normal;
        var stretch = FontStretch.Normal;

        if (style.TryGet(fontWeight, out var weightValue)) {
            var text = Styles.Values.NameOf(weightValue);

            weight = text switch {
                "bold" => 700,
                "normal" => 400,
                _ => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? Math.Clamp(parsed, 1, 1000)
                    : 400
            };
        }

        if (style.TryGet(fontStyle, out var slantValue)) {
            slant = Styles.Values.NameOf(slantValue) switch {
                "italic" => FontStyle.Italic,
                "oblique" => FontStyle.Oblique,
                _ => FontStyle.Normal
            };
        }

        if (style.TryGet(fontStretch, out var stretchValue)) {
            stretch = Styles.Values.NameOf(stretchValue) switch {
                "ultra-condensed" => FontStretch.UltraCondensed,
                "extra-condensed" => FontStretch.ExtraCondensed,
                "condensed" => FontStretch.Condensed,
                "semi-condensed" => FontStretch.SemiCondensed,
                "semi-expanded" => FontStretch.SemiExpanded,
                "expanded" => FontStretch.Expanded,
                "extra-expanded" => FontStretch.ExtraExpanded,
                "ultra-expanded" => FontStretch.UltraExpanded,
                _ => FontStretch.Normal
            };
        }

        return new FontQuery(weight, slant, stretch);
    }

    UiElement? HitTest(UiElement element, float x, float y) {
        var inside = Contains(element, x, y);

        // ⚠ Being outside an element is not a reason to skip its children. `overflow: visible` is
        // CSS's default and means exactly that a child may hang outside its parent and still be
        // drawn — so it must still be clickable. Returning early on `!inside` would make every
        // overflowing element, every dropdown and every tooltip unhittable, and the bug would look
        // like the click landing on whatever is behind them.
        if (!inside && Clips(element)) {
            return null;
        }

        for (var i = element.Children.Count - 1; i >= 0; i--) {
            if (HitTest(element.Children[i], x, y) is { } hit) {
                return hit;
            }
        }

        return inside && element.IsHitTestVisible ? element : null;
    }

    /// <summary>Whether an element cuts off what hangs outside it.</summary>
    /// <remarks>
    ///     The clip is asked about on the <i>parent</i>, because it is the parent that clips and the
    ///     child has no idea it is being cut.
    /// </remarks>
    bool Clips(UiElement element) =>
        element.Style.TryGet(overflow, out var value) && value != visible;

    static bool Contains(UiElement element, float x, float y) =>
        x >= element.AbsoluteLeft
        && y >= element.AbsoluteTop
        && x < element.AbsoluteLeft + element.Width
        && y < element.AbsoluteTop + element.Height;

    /// <summary>Turns the parent-relative layout results into document-space rectangles.</summary>
    /// <remarks>
    ///     Accumulated once per pass rather than walked per query. Hit testing asks for absolute
    ///     bounds several times per pointer move, and the draw list will ask for every element's
    ///     every frame; a walk to the root per read is the same arithmetic done depth times over.
    /// </remarks>
    static void Accumulate(UiElement element, float x, float y) {
        // ⚠ The offset lands here and nowhere else, which is what makes it free. Every consumer of a
        // position — hit testing, the draw list, arrow navigation — reads the accumulated value, so
        // a shifted element is drawn, clicked and navigated to in its shifted place without any of
        // them being told that shifting is a thing that can happen.
        element.AbsoluteLeft = x + element.Left + element.OffsetX;
        element.AbsoluteTop = y + element.Top + element.OffsetY;

        foreach (var child in element.Children) {
            Accumulate(child, element.AbsoluteLeft, element.AbsoluteTop);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Layout.Dispose();
}
