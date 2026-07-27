// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>One node of the user interface.</summary>
/// <remarks>
///     <para>
///         <b>Elements are classes, and that is a deliberate departure from the rest of the
///         engine.</b> An ECS component is a struct because there are a million of them in a hot
///         loop; a UI node has identity, virtual behaviour and event handlers, and there are ten
///         thousand of them. The struct-of-arrays discipline lives where the loops actually are —
///         the layout store and, later, the draw list — and this type is a handle into them.
///     </para>
///     <para>
///         It holds no geometry and no style of its own. The cascade owns the computed style, the
///         layout tree owns the result, and everything read from here is a lookup into one of the
///         two. That is what keeps a hundred identical list rows from being a hundred copies of
///         anything.
///     </para>
/// </remarks>
public class UiElement {
    readonly List<UiElement> children = [];

    internal UiElement(UiDocument document, string tag, UiElement? parent, StyleNodeId styleNode, LayoutNodeId layoutNode) {
        Document = document;
        Tag = tag;
        Parent = parent;
        StyleNode = styleNode;
        LayoutNode = layoutNode;
        Style = ComputedStyle.Empty;
    }

    /// <summary>The document this belongs to.</summary>
    public UiDocument Document { get; }

    /// <summary>Its element name, which selectors match on.</summary>
    public string Tag { get; }

    /// <summary>Its parent, or <c>null</c> for the root.</summary>
    public UiElement? Parent { get; }

    /// <summary>Its children, in document order.</summary>
    public IReadOnlyList<UiElement> Children => children;

    /// <summary>What the cascade decided. Interned, so two alike elements share one object.</summary>
    public ComputedStyle Style { get; internal set; }

    /// <summary>Its resolved font size in pixels, which every <c>em</c> on it measures against.</summary>
    public float FontSize { get; internal set; } = LengthContext.InitialFontSize;

    internal StyleNodeId StyleNode { get; }

    internal LayoutNodeId LayoutNode { get; }

    /// <summary>Its left edge, relative to its parent, after the last layout pass.</summary>
    public float Left => Document.Layout.GetLeft(LayoutNode);

    /// <summary>Its top edge, relative to its parent.</summary>
    public float Top => Document.Layout.GetTop(LayoutNode);

    /// <summary>Its width.</summary>
    public float Width => Document.Layout.GetWidth(LayoutNode);

    /// <summary>Its height.</summary>
    public float Height => Document.Layout.GetHeight(LayoutNode);

    /// <summary>Adds a child element.</summary>
    /// <param name="tag">Its element name.</param>
    /// <param name="id">Its identifier, for an <c>#id</c> selector.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The new element.</returns>
    public UiElement Add(string tag, string? id = null, params ReadOnlySpan<string> classNames) =>
        Document.Create(tag, this, id, classNames);

    /// <summary>Adds a class, and invalidates what that could have changed.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was not already there.</returns>
    public bool AddClass(string className) {
        if (!Document.Styles.Tree.AddClass(StyleNode, className)) {
            return false;
        }

        Document.Invalidate();
        return true;
    }

    /// <summary>Removes a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveClass(string className) {
        if (!Document.Styles.Tree.RemoveClass(StyleNode, className)) {
            return false;
        }

        Document.Invalidate();
        return true;
    }

    /// <summary>Whether it carries a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it does.</returns>
    public bool HasClass(string className) => Document.Styles.Tree.HasClass(StyleNode, className);

    /// <summary>Its interaction state — hover, focus, active — which selectors match on.</summary>
    public ElementState State {
        get => Document.Styles.Tree.GetState(StyleNode);
        set {
            if (Document.Styles.Tree.GetState(StyleNode) == value) {
                return;
            }

            Document.Styles.Tree.SetState(StyleNode, value);
            Document.Invalidate();
        }
    }

    internal void Attach(UiElement child) => children.Add(child);
}
