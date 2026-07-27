// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

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
///         ⚠ <b>The tree is append-only</b>, because <see cref="StyleTree" /> is: elements are
///         created parents-first and never removed. That is enough to lay out a document and not
///         enough to run an application, and removal is owed with the rest of the element tree.
///         Said plainly rather than left for someone to discover.
///     </para>
/// </remarks>
public sealed class UiDocument : IDisposable {
    readonly List<UiElement> elements = [];
    readonly List<ComputedStyle?> appliedStyles = [];
    readonly List<float> appliedFontSizes = [];
    readonly DrawListBuilder drawings;
    readonly int pointerEvents;
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
        for (var i = 0; i < appliedStyles.Count; i++) {
            appliedStyles[i] = null;
        }

        Invalidate();
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

        elements.Add(element);
        appliedStyles.Add(null);
        appliedFontSizes.Add(float.NaN);

        Invalidate();
        return element;
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

        // Parents before children, which ascending index already is: elements are created
        // parents-first and the style tree indexes them in creation order.
        for (var i = 0; i < elements.Count; i++) {
            var element = elements[i];
            var style = computed[element.StyleNode.Index];
            var parentFontSize = element.Parent?.FontSize ?? Viewport.RootFontSize;

            element.Style = style;
            element.FontSize = Builder.ResolveFontSize(style, parentFontSize, Viewport);

            // ⚠ Reference equality, which is the whole reason ComputedStyle is interned. Two
            // elements that resolved alike hold the same object, so this is one pointer comparison
            // rather than a walk of a property table — and a table of ten thousand identical cells
            // rebuilds nothing.
            //
            // The font size has to be part of the test as well as the style: an element whose own
            // declarations did not change still needs rebuilding if an ancestor's font size did,
            // because every `em` on it measures against a different number now.
            if (ReferenceEquals(appliedStyles[i], style) && appliedFontSizes[i].Equals(element.FontSize)) {
                continue;
            }

            appliedStyles[i] = style;
            appliedFontSizes[i] = element.FontSize;
            StylesApplied++;

            var layoutStyle = Builder.Build(style, Viewport.WithFontSize(element.FontSize));
            Layout.SetStyle(element.LayoutNode, layoutStyle);
        }

        Layout.CalculateLayout(Root.LayoutNode, Viewport.ViewportWidth, Viewport.ViewportHeight, Direction.Ltr);
        Accumulate(Root, 0f, 0f);
        return true;
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
        return target;
    }

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

    internal bool PointerEventsNone(ComputedStyle style) =>
        style.TryGet(pointerEvents, out var value) && value == none;

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
