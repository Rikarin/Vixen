// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

public sealed partial class UiDocument {
    /// <summary>Moves an element, and everything under it, to a different parent.</summary>
    /// <param name="element">The element to move.</param>
    /// <param name="parent">Where it should end up.</param>
    /// <param name="index">Where among its new siblings, or -1 for the end.</param>
    /// <remarks>
    ///     <para>
    ///         <b>What docking, drag-and-drop between lists and a tab torn out of one group into
    ///         another all need</b>, and what <see cref="Move" /> deliberately refuses: that one
    ///         reorders within a parent, because a style slot's <i>position</i> is what three passes
    ///         read as depth order, and a child whose slot sits above its new parent's breaks every
    ///         one of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the slots are rebuilt rather than moved.</b> The subtree gets fresh style
    ///         nodes under the new parent, in pre-order, carrying its tags, ids, classes, states,
    ///         attributes, inline blocks and whether it holds text; the old slots are tombstoned and
    ///         reclaimed by the next compaction. The <i>elements</i> are untouched — same instances,
    ///         same handlers, same layout nodes, same children, same focus — which is the entire
    ///         point: a docked panel that was rebuilt instead of moved would lose its scroll
    ///         position, its selection and whatever the user had typed into it.
    ///     </para>
    ///     <para>
    ///         The cost is O(subtree) slot creations per move plus the same number of tombstones.
    ///         Reparenting is a user action — a drag that ends — so it is paid at human speed, and
    ///         the tombstones are exactly what the compaction heuristic already exists to notice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layout node is moved rather than rebuilt</b>, unlike the style slot. It has
    ///         no ordering invariant to break — a layout tree is a tree and nothing reads its
    ///         indices as depth — and rebuilding it would throw away the measurement cache for
    ///         every node in the subtree.
    ///     </para>
    /// </remarks>
    public void Reparent(UiElement element, UiElement parent, int index = -1) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(parent);

        if (ReferenceEquals(element, Root)) {
            throw new InvalidOperationException("the root has no parent to move it to — a document is its tree.");
        }

        if (!ReferenceEquals(element.Document, this) || !ReferenceEquals(parent.Document, this)) {
            throw new ArgumentException("both elements have to belong to this document.");
        }

        for (var walk = parent; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, element)) {
                throw new InvalidOperationException(
                    "an element cannot be moved inside itself — the result would not be a tree."
                );
            }
        }

        var previous = element.Parent
            ?? throw new InvalidOperationException("that element is not in the tree.");

        var target = index < 0 ? parent.Children.Count : index;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, parent.Children.Count);

        if (ReferenceEquals(previous, parent)) {
            // The same parent is a reorder, and reordering already has an answer that does not
            // rebuild a single slot. Falling through would work and would be strictly worse.
            Move(element, Math.Min(target, parent.Children.Count - 1));
            return;
        }

        var old = element.StyleNode;

        previous.Detach(element);
        Layout.RemoveChild(previous.LayoutNode, element.LayoutNode);

        parent.Insert(element, target);
        Layout.InsertChild(parent.LayoutNode, element.LayoutNode, target);
        element.Adopt(parent);

        Restyle(element, parent.StyleNode);

        // ⚠ After the subtree has been given its new slots, not before. `Remove` kills the whole run
        // below it, so removing first would tombstone slots this is still reading tags and classes
        // out of.
        Styles.Tree.Remove(old);
        Invalidate();
    }

    /// <summary>Gives a subtree fresh style slots under a new parent, in pre-order.</summary>
    /// <remarks>
    ///     Pre-order because that is the invariant being preserved: a parent's slot has to come
    ///     before its children's, and creating them in the order they are walked is that by
    ///     construction rather than by arithmetic.
    /// </remarks>
    void Restyle(UiElement element, StyleNodeId parent) {
        var tree = Styles.Tree;
        var source = element.StyleNode;

        var created = tree.CreateElement(
            element.Tag,
            parent,
            tree.GetId(source),
            tree.GetClassNames(source)
        );

        tree.SetState(created, tree.GetState(source));
        tree.SetInlineStyle(created, tree.GetInlineStyle(source));
        tree.SetHasText(created, tree.HasText(source));

        foreach (var (name, value) in tree.GetAttributes(source)) {
            tree.SetAttribute(created, name, value);
        }

        element.Restyle(created);

        foreach (var child in element.Children) {
            Restyle(child, created);
        }
    }
}
