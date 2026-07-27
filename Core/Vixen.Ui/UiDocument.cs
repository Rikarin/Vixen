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
    bool dirty = true;

    /// <summary>Creates a document over a surface of a given size.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rootFontSize">The font size <c>rem</c> measures against.</param>
    public UiDocument(float width, float height, float rootFontSize = LengthContext.InitialFontSize) {
        Styles = new StyleEngine();
        Layout = new LayoutTree();
        Builder = new LayoutStyleBuilder(Styles.Properties, Styles.Values, Styles.Names);
        Viewport = LengthContext.ForViewport(width, height, rootFontSize);
        Root = Create("root", null, null, []);
    }

    /// <summary>The cascade.</summary>
    public StyleEngine Styles { get; }

    /// <summary>The flexbox engine.</summary>
    public LayoutTree Layout { get; }

    /// <summary>The step between them.</summary>
    public LayoutStyleBuilder Builder { get; }

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
    public UiElement Create(string tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames) {
        ArgumentNullException.ThrowIfNull(tag);

        var styleNode = Styles.Tree.CreateElement(tag, parent?.StyleNode, id, classNames);
        var layoutNode = Layout.CreateNode();

        var element = new UiElement(this, tag, parent, styleNode, layoutNode);

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
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => Layout.Dispose();
}
