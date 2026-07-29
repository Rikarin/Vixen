// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One item of a <see cref="TreeView" />.</summary>
/// <remarks>
///     <para>
///         <b>A model object, not an element.</b> A tree of a hundred thousand nodes has a hundred
///         thousand of these and perhaps thirty rows on screen; making the node an element would put
///         a style slot, a layout node and a draw command behind every one of them, which is exactly
///         what doc 09 makes virtualisation a first-class primitive to avoid.
///     </para>
///     <para>
///         ⚠ <b><see cref="HasChildren" /> is a claim, not a count.</b> A node that has not been
///         populated still has to draw a chevron — the project browser cannot enumerate a folder to
///         find out whether it is worth showing one — so "might have children" and "has children
///         loaded" are two different questions, and only the first is answerable cheaply.
///     </para>
/// </remarks>
public sealed class TreeNode {
    readonly List<TreeNode> children = [];
    bool populated;

    /// <summary>Creates a node.</summary>
    /// <param name="text">What it says.</param>
    /// <param name="tag">Whatever the application wants to hang off it.</param>
    public TreeNode(string? text = null, object? tag = null) {
        Text = text;
        Tag = tag;
    }

    /// <summary>What it says.</summary>
    public string? Text { get; set; }

    /// <summary>Whatever the application hung off it.</summary>
    public object? Tag { get; set; }

    /// <summary>The glyph drawn between the chevron and the text, or <c>null</c> for none.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="PathBuilder" /> rather than an icon id, which is the same bargain
    ///     <c>Icon</c> itself makes.</b> A tree has no icon set and should not acquire one: what a light, a folder
    ///     or a shader looks like is the application's vocabulary, and a control that knew would be
    ///     the wrong place for it. What the control owns is the <i>column</i> — a row whose glyph is
    ///     null still reserves it, so a tree where only some rows have one does not have two text
    ///     alignments down its left edge.
    /// </remarks>
    public PathBuilder? Icon { get; set; }

    /// <summary>The node this is under, or <c>null</c> for a root.</summary>
    public TreeNode? Parent { get; private set; }

    /// <summary>Its children, in order.</summary>
    public IReadOnlyList<TreeNode> Children => children;

    /// <summary>Whether its children are showing.</summary>
    public bool IsExpanded { get; internal set; }

    /// <summary>Whether it may have children, whether or not any have been loaded.</summary>
    /// <remarks>
    ///     Defaults to whether it has any. Set it directly for a node whose children are fetched on
    ///     demand — a folder nobody has opened yet — so that it draws a chevron and can be expanded.
    /// </remarks>
    public bool HasChildren {
        get => field || children.Count > 0;
        set;
    }

    /// <summary>What to run the first time it is expanded, if its children are loaded on demand.</summary>
    /// <remarks>
    ///     ⚠ <b>Run once.</b> A populate that ran on every expansion would append the same children
    ///     again each time somebody folded a folder and unfolded it — the classic duplicated-tree
    ///     bug, and the reason this is a callback with a flag rather than an event.
    /// </remarks>
    public Action<TreeNode>? Populate { get; set; }

    /// <summary>How deep it is. A root is zero.</summary>
    public int Depth {
        get {
            var depth = 0;

            for (var walk = Parent; walk is not null; walk = walk.Parent) {
                depth++;
            }

            return depth;
        }
    }

    /// <summary>Adds a child.</summary>
    /// <param name="child">The child.</param>
    /// <param name="index">Where among the others, or -1 for the end.</param>
    /// <returns>The child.</returns>
    public TreeNode Add(TreeNode child, int index = -1) {
        ArgumentNullException.ThrowIfNull(child);

        child.Parent?.children.Remove(child);
        child.Parent = this;

        children.Insert(index < 0 || index > children.Count ? children.Count : index, child);
        return child;
    }

    /// <summary>Adds a child with some text.</summary>
    /// <param name="text">What it says.</param>
    /// <param name="tag">Whatever the application wants to hang off it.</param>
    /// <returns>The child.</returns>
    public TreeNode Add(string? text, object? tag = null) => Add(new TreeNode(text, tag));

    /// <summary>Where a child sits among the others, or -1 if it is not one.</summary>
    /// <param name="child">The child.</param>
    /// <returns>The index.</returns>
    public int IndexOf(TreeNode child) => children.IndexOf(child);

    /// <summary>Takes a child out.</summary>
    /// <param name="child">The child.</param>
    /// <returns>Whether it was one.</returns>
    public bool Remove(TreeNode child) {
        ArgumentNullException.ThrowIfNull(child);

        if (!children.Remove(child)) {
            return false;
        }

        child.Parent = null;
        return true;
    }

    /// <summary>Whether a node is this one or somewhere under it.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    ///     What a drop has to ask before it moves anything. Dragging a folder into its own subfolder
    ///     is a gesture users make by accident and a cycle the tree cannot represent.
    /// </remarks>
    public bool Contains(TreeNode node) {
        for (var walk = node; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, this)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Loads the children, if they are loaded on demand and have not been.</summary>
    internal void EnsurePopulated() {
        if (populated || Populate is not { } populate) {
            return;
        }

        populated = true;
        populate(this);
    }
}
