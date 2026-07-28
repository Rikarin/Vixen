// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    readonly int letterSpacing;
    readonly int lineHeight;
    readonly int zIndex;
    readonly int fontWeight;
    readonly int fontStyle;
    readonly int bold;
    readonly int italic;
    readonly int oblique;
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
    bool dirty = true;

    /// <summary>Creates a document over a surface of a given size.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rootFontSize">The font size <c>rem</c> measures against.</param>
    public UiDocument(float width, float height, float rootFontSize = LengthContext.InitialFontSize) {
        Styles = new StyleEngine();
        Layout = new LayoutTree();
        Builder = new LayoutStyleBuilder(Styles.Properties, Styles.Values, Styles.Names);
        drawings = new DrawListBuilder(Styles.Properties, Styles.Values, Styles.Names);
        Viewport = LengthContext.ForViewport(width, height, rootFontSize);

        reader = new StyleValueParser(Styles.Values, Styles.Names);

        pointerEvents = Styles.Properties.Intern("pointer-events");
        color = Styles.Properties.Intern("color");
        fontFamily = Styles.Properties.Intern("font-family");
        letterSpacing = Styles.Properties.Intern("letter-spacing");
        lineHeight = Styles.Properties.Intern("line-height");
        zIndex = Styles.Properties.Intern("z-index");
        fontWeight = Styles.Properties.Intern("font-weight");
        fontStyle = Styles.Properties.Intern("font-style");
        bold = Styles.Values.Intern("bold");
        italic = Styles.Values.Intern("italic");
        oblique = Styles.Values.Intern("oblique");
        overflow = Styles.Properties.Intern("overflow");
        none = Styles.Values.Intern("none");
        visible = Styles.Values.Intern("visible");
        InternCursors();

        Root = Create("root", null, null, []);
    }

    /// <summary>The cascade.</summary>
    public StyleEngine Styles { get; }

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

    /// <summary>Marks the document as needing a fresh pass.</summary>
    public void Invalidate() => dirty = true;

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

        if (!ReferenceEquals(element.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(element));
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

        // ⚠ Before anything reads a slot, and only when the tombstones outnumber the elements. Here
        // rather than in `Remove`, because compaction is O(elements) and removing a thousand-row list
        // one row at a time would then be O(elements²) — and because a pass is the one moment where
        // every id is about to be re-read anyway, so nothing is holding a stale one across it.
        //
        // The floor stops a document with four elements compacting because it removed three.
        if (Styles.Tree.DeadCount >= CompactionFloor && Styles.Tree.DeadCount > Styles.Tree.LiveCount) {
            CompactStyles();
        }

        var computed = Styles.ResolveAll();
        Apply(computed, Root, Viewport.RootFontSize, ComputedText.Initial);

        Layout.CalculateLayout(Root.LayoutNode, Viewport.ViewportWidth, Viewport.ViewportHeight, Direction.Ltr);
        Accumulate(Root, 0f, 0f);
        return true;
    }

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
    /// <summary>The text properties that are inherited computed rather than as written.</summary>
    /// <param name="LineHeight">
    ///     The ancestor's resolved line height in pixels, or NaN when it was unitless or unset.
    /// </param>
    /// <param name="LineHeightFactor">
    ///     The multiple a unitless <c>line-height</c> named, or NaN. Kept apart from the pixels
    ///     because the difference is the whole point of the unitless form: <c>1.5</c> inherits as the
    ///     number and multiplies each descendant's own font size, where <c>1.5em</c> inherits as the
    ///     length the ancestor resolved once.
    /// </param>
    /// <param name="LetterSpacing">The ancestor's resolved letter spacing in pixels.</param>
    readonly record struct ComputedText(float LineHeight, float LineHeightFactor, float LetterSpacing) {
        /// <summary>What the root starts with: the font's own line height and no tracking.</summary>
        public static ComputedText Initial => new(float.NaN, float.NaN, 0f);
    }

    void Apply(ComputedStyle[] computed, UiElement element, float parentFontSize, ComputedText parentText) {
        var style = computed[element.StyleNode.Index];

        element.Style = style;
        element.FontSize = Builder.ResolveFontSize(style, parentFontSize, Viewport);

        // ⚠ After the font size and before the children, because both of these resolve against *this*
        // element's size and both are handed down in the form they came out as.
        var text = ResolveText(style, element.FontSize, parentText);

        element.LineHeight = float.IsNaN(text.LineHeightFactor)
            ? text.LineHeight
            : text.LineHeightFactor * element.FontSize;

        element.LetterSpacing = text.LetterSpacing;

        // Resolved here rather than read in the draw list, because hit testing needs the same answer
        // and reaching it would mean parsing the same declaration twice per frame from two places
        // that could disagree. The setter invalidates the parent's paint order when it changes.
        element.ZIndex = ZIndexOf(style);

        // ⚠ Reference equality, which is the whole reason ComputedStyle is interned. Two elements
        // that resolved alike hold the same object, so this is one pointer comparison rather than a
        // walk of a property table — and a table of ten thousand identical cells rebuilds nothing.
        //
        // The font size has to be part of the test as well as the style: an element whose own
        // declarations did not change still needs rebuilding if an ancestor's font size did, because
        // every `em` on it measures against a different number now.
        //
        if (!ReferenceEquals(element.AppliedStyle, style) || !element.AppliedFontSize.Equals(element.FontSize)) {
            element.AppliedStyle = style;
            element.AppliedFontSize = element.FontSize;
            StylesApplied++;

            Layout.SetStyle(element.LayoutNode, Builder.Build(style, Viewport.WithFontSize(element.FontSize)));
        }

        // ⚠ Separately, because these change what the element *measures* rather than what its box is
        // — and the layout tree finds out about a changed measurement only by being told. They are
        // also inherited outside the cascade, so a label whose *parent* changed `line-height` has an
        // unchanged ComputedStyle: the reference test above passes, `SetStyle` is never reached, and
        // the label would keep measuring itself at the old height for the rest of its life.
        //
        // `.Equals` rather than `==`, because NaN is a legitimate value here and NaN == NaN is false.
        if (!element.AppliedLineHeight.Equals(element.LineHeight)
            || !element.AppliedLetterSpacing.Equals(element.LetterSpacing)) {
            element.AppliedLineHeight = element.LineHeight;
            element.AppliedLetterSpacing = element.LetterSpacing;

            // Only a node that measures itself, which is what having text means — and what
            // `MarkDirty` insists on, on the grounds that nothing else about a node can change
            // without a style or a child changing and both of those already mark it. An element
            // with no text has no measurement for these to have changed, only descendants that do.
            if (!string.IsNullOrEmpty(element.Text)) {
                Layout.MarkDirty(element.LayoutNode);
            }
        }

        foreach (var child in element.Children) {
            Apply(computed, child, element.FontSize, text);
        }
    }

    /// <summary>Computes the text properties that are inherited resolved rather than as written.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <param name="fontSize">Its own font size, which every relative unit here measures against.</param>
    /// <param name="parent">What its parent came out with.</param>
    /// <returns>What it comes out with, and what its children inherit.</returns>
    /// <remarks>
    ///     An element that declares nothing passes its parent's answer straight through, which is
    ///     what makes this inheritance rather than a default — and passes the <i>factor</i> through
    ///     as a factor, so a unitless <c>1.5</c> on a panel is one and a half times each descendant's
    ///     own size rather than one and a half times the panel's.
    /// </remarks>
    ComputedText ResolveText(ComputedStyle style, float fontSize, ComputedText parent) {
        var lineHeight = parent.LineHeight;
        var factor = parent.LineHeightFactor;
        var tracking = parent.LetterSpacing;

        if (style.TryGet(this.lineHeight, out var declared)) {
            var value = reader.Parse(declared);

            switch (value.Kind) {
                // Unitless, and the one that stays a number. `line-height: 1.5` is a ratio every
                // descendant applies to itself.
                case StyleValueKind.Number:
                    lineHeight = float.NaN;
                    factor = value.Number;
                    break;

                // ⚠ A percentage is *not* the unitless form. `150%` resolves against this element's
                // font size once and inherits as that length, which is precisely the trap the
                // unitless form exists to avoid. Handled apart from the other units because
                // `LengthContext` deliberately refuses to resolve a percentage — there it means the
                // containing block, which only layout knows. On `line-height` it means the font size,
                // and that is known right here.
                case StyleValueKind.Length when value.Unit == StyleUnit.Percent:
                    lineHeight = value.Number / 100f * fontSize;
                    factor = float.NaN;
                    break;

                case StyleValueKind.Length:
                    lineHeight = value.Number * Viewport.WithFontSize(fontSize).PixelsPer(value.Unit);
                    factor = float.NaN;
                    break;

                // `normal`, and anything else with no reading — the font's own recommendation.
                default:
                    lineHeight = float.NaN;
                    factor = float.NaN;
                    break;
            }
        }

        if (style.TryGet(letterSpacing, out var spacing)) {
            var value = reader.Parse(spacing);

            tracking = value.Kind == StyleValueKind.Length
                ? value.Number * Viewport.WithFontSize(fontSize).PixelsPer(value.Unit)
                : 0f;
        }

        return new ComputedText(lineHeight, factor, tracking);
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

    /// <summary>An element's <c>z-index</c>, which is zero when it has none.</summary>
    /// <remarks>
    ///     <c>auto</c> is a keyword rather than a number and so reads as zero, which is right here:
    ///     what <c>auto</c> means in CSS is "take the stacking context's own level", and sibling
    ///     ordering has no stacking context to take a level from.
    /// </remarks>
    int ZIndexOf(ComputedStyle style) =>
        style.TryGet(zIndex, out var id) && reader.Parse(id) is { Kind: StyleValueKind.Number } value
            ? (int) value.Number
            : 0;

    internal string? FontFamilyOf(ComputedStyle style) =>
        style.TryGet(fontFamily, out var value) ? Styles.Values.NameOf(value) : null;

    /// <summary>An element's <c>font-weight</c> on CSS's 1–1000 scale.</summary>
    /// <remarks>
    ///     ⚠ <c>lighter</c> and <c>bolder</c> are <b>not</b> read, and fall through to regular. They
    ///     are relative to the <i>parent's computed</i> weight, which this cascade does not have —
    ///     it inherits specified values, so the parent's declaration might itself be <c>bolder</c>
    ///     and the chain has no bottom. Owed with the computed-value stage, alongside
    ///     <c>line-height</c>, and left out rather than approximated as "one step from 400", which
    ///     would be right only for an element whose parent said nothing.
    /// </remarks>
    internal int FontWeightOf(ComputedStyle style) {
        if (!style.TryGet(fontWeight, out var id)) {
            return FontRegistry.RegularWeight;
        }

        var value = reader.Parse(id);

        if (value.Kind == StyleValueKind.Number) {
            return Math.Clamp((int) value.Number, 1, 1000);
        }

        return id == bold ? FontRegistry.BoldWeight : FontRegistry.RegularWeight;
    }

    /// <summary>An element's <c>font-style</c>.</summary>
    internal FontStyle FontStyleOf(ComputedStyle style) {
        if (!style.TryGet(fontStyle, out var id)) {
            return FontStyle.Normal;
        }

        return id == italic ? FontStyle.Italic : id == oblique ? FontStyle.Oblique : FontStyle.Normal;
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

        // Backwards through the *paint* order, so the element on top is the one a click lands on. In
        // document order these are the same walk; with a `z-index` in play they are not, and a hit
        // test that kept its own opinion would send the click to whatever the lifted child covers.
        var order = element.PaintOrder;

        for (var i = order.Count - 1; i >= 0; i--) {
            if (HitTest(order[i], x, y) is { } hit) {
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
