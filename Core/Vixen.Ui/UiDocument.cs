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
    readonly int overflow;
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

        pointerEvents = Styles.Properties.Intern("pointer-events");
        fontFamily = Styles.Properties.Intern("font-family");
        overflow = Styles.Properties.Intern("overflow");
        none = Styles.Values.Intern("none");
        visible = Styles.Values.Intern("visible");

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
    public void Load(string css, StyleOrigin origin = StyleOrigin.Author) {
        Styles.Load(css, origin);
        Invalidate();
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
    /// <param name="tag">Its element name.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    public T Create<T>(string tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames)
        where T : UiElement, new() {
        ArgumentNullException.ThrowIfNull(tag);

        var styleNode = Styles.Tree.CreateElement(tag, parent?.StyleNode, id, classNames);
        var layoutNode = Layout.CreateNode();

        var element = new T();
        element.Bind(this, tag, parent, styleNode, layoutNode);

        if (parent is not null) {
            parent.Attach(element);
            Layout.AddChild(parent.LayoutNode, layoutNode);
        }

        Invalidate();
        return element;
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
    }

    /// <summary>Marks a subtree as no longer part of any document.</summary>
    static void Retire(UiElement element) {
        element.Retire();

        foreach (var child in element.Children) {
            Retire(child);
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

        var computed = Styles.ResolveAll();
        Apply(computed, Root, Viewport.RootFontSize);

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
    void Apply(ComputedStyle[] computed, UiElement element, float parentFontSize) {
        var style = computed[element.StyleNode.Index];

        element.Style = style;
        element.FontSize = Builder.ResolveFontSize(style, parentFontSize, Viewport);

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
            Apply(computed, child, element.FontSize);
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
        element.AbsoluteLeft = x + element.Left;
        element.AbsoluteTop = y + element.Top;

        foreach (var child in element.Children) {
            Accumulate(child, element.AbsoluteLeft, element.AbsoluteTop);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Layout.Dispose();
}
