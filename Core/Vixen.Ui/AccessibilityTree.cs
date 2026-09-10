// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>One relation of a captured node, with the other end named by node id.</summary>
/// <param name="Relation">What kind of link it is.</param>
/// <param name="Target">
///     The node on the other end, or <c>0</c> where the element it points at is not a node of this
///     capture — an element with no role, or one outside the captured root.
/// </param>
/// <remarks>
///     <see cref="AccessibleRelationship" /> with the <see cref="UiElement" /> taken out of it, which
///     is the whole difference between the live tree and a capture of one.
/// </remarks>
public readonly record struct AccessibilityLink(AccessibleRelation Relation, int Target);

/// <summary>What kind of change a node underwent between two captures.</summary>
public enum AccessibilityChangeKind : byte {
    /// <summary>It was not in the previous capture. AT-SPI <c>children-changed:add</c>.</summary>
    Added,

    /// <summary>It is not in this one. AT-SPI <c>children-changed:remove</c>.</summary>
    Removed,

    /// <summary>It is in both and something about it differs — <see cref="AccessibilityChange.Fields" /> says what.</summary>
    Changed
}

/// <summary>Which parts of a node differ, for a bridge that posts one notification per property.</summary>
/// <remarks>
///     ⚠ <b>A flag set rather than a list of before-and-after pairs, because that is the shape all
///     three platforms want.</b> <c>NSAccessibility</c> posts
///     <c>NSAccessibilityValueChangedNotification</c> and AppKit then comes back and <i>asks</i> for
///     the value; UIA's <c>UiaRaiseAutomationPropertyChangedEvent</c> wants the property id; AT-SPI's
///     <c>object:property-change</c> carries a name. None of the three needs the old value, and
///     carrying one would double the size of the diff for a consumer that throws it away.
/// </remarks>
[Flags]
public enum AccessibilityFields : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><see cref="AccessibilityNode.Role" />.</summary>
    Role = 1 << 0,

    /// <summary><see cref="AccessibilityNode.Name" />.</summary>
    Name = 1 << 1,

    /// <summary><see cref="AccessibilityNode.Description" />.</summary>
    Description = 1 << 2,

    /// <summary><see cref="AccessibilityNode.Value" />.</summary>
    Value = 1 << 3,

    /// <summary><see cref="AccessibilityNode.States" />.</summary>
    States = 1 << 4,

    /// <summary><see cref="AccessibilityNode.Bounds" />.</summary>
    Bounds = 1 << 5,

    /// <summary><see cref="AccessibilityNode.Links" />.</summary>
    Links = 1 << 6,

    /// <summary>Which nodes are its children, or in what order. AT-SPI <c>children-changed</c>.</summary>
    Children = 1 << 7,

    /// <summary>It moved to a different parent, which every bridge treats as a reparent rather than an edit.</summary>
    Parent = 1 << 8
}

/// <summary>One node's worth of change between two captures.</summary>
/// <param name="Kind">Added, removed, or edited in place.</param>
/// <param name="Node">The node id, which is stable across the captures of one
/// <see cref="AccessibilityTreeSource" />.</param>
/// <param name="Fields">
///     For <see cref="AccessibilityChangeKind.Changed" />, which parts differ — never
///     <see cref="AccessibilityFields.None" />. Always <see cref="AccessibilityFields.None" /> for
///     the other two, where every part is new or gone.
/// </param>
public readonly record struct AccessibilityChange(
    AccessibilityChangeKind Kind,
    int Node,
    AccessibilityFields Fields
);

/// <summary>One node of a captured accessibility tree: data, with no element behind it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here refers to a <see cref="UiElement" />, and that is the point rather than
///         tidiness.</b> <c>Vixen.Ui</c>'s graph is single-threaded by contract, and every platform
///         bridge is asked questions from somewhere else — AT-SPI's D-Bus calls arrive on another
///         thread, and AppKit asks on the main one whether or not that is the thread that owns the
///         document. A bridge that answered by walking live elements would be reading a tree while
///         another thread mutated it; a bridge that answers out of one of these is reading a frame
///         that has already happened.
///     </para>
///     <para>
///         The properties are the ones every bridge needs and no more: the accname, the value, the
///         computed states, the relations and the bounds. Anything a control knows that is not on
///         this list is not reaching a screen reader today whatever the control does with it.
///     </para>
/// </remarks>
public sealed class AccessibilityNode {
    internal AccessibilityNode(int id, UiElement element) {
        Id = id;
        Tag = element.Tag;
        Role = element.Role;
        Name = element.AccessibleName;
        Description = element.AccessibleDescription;
        Value = element.AccessibleValue;
        States = element.AccessibleState;
        Bounds = element.Bounds;
    }

    /// <summary>This node's identity, stable across the captures of one source, and never <c>0</c>.</summary>
    public int Id { get; }

    /// <summary>The tag of the element it was taken from. Diagnostics only — no bridge should map on it.</summary>
    public string Tag { get; }

    /// <summary>What kind of thing it is. Never <see cref="AccessibleRole.None" />: those are not nodes.</summary>
    public AccessibleRole Role { get; }

    /// <summary>What a screen reader calls it.</summary>
    public string? Name { get; }

    /// <summary>What a screen reader adds after the name.</summary>
    public string? Description { get; }

    /// <summary>What it currently holds, for the roles that hold something.</summary>
    public string? Value { get; }

    /// <summary>Every state bit, computed at the moment of capture.</summary>
    public AccessibleStates States { get; }

    /// <summary>Where it is, in the document's coordinate space.</summary>
    /// <remarks>
    ///     ⚠ <b>Document space, so every bridge owes a transform.</b> AppKit wants screen coordinates
    ///     with the origin at the bottom-left of the main display, UIA wants screen coordinates
    ///     top-left, AT-SPI offers both. Converting here would pick one of the three and be wrong for
    ///     the other two — and would need a window, which the capture deliberately does not have.
    /// </remarks>
    public Rectangle Bounds { get; }

    /// <summary>The node this one hangs under, or <c>0</c> if it is a root of the capture.</summary>
    public int Parent { get; internal set; }

    /// <summary>Its children in the accessibility tree, which is not the element tree.</summary>
    public IReadOnlyList<AccessibilityNode> Children { get; internal set; } = [];

    /// <summary>Its relations, with the far ends resolved to node ids.</summary>
    public IReadOnlyList<AccessibilityLink> Links { get; internal set; } = [];
}

/// <summary>The accessibility tree of a document at one moment, as data a bridge can answer from.</summary>
/// <remarks>
///     <para>
///         <b>This is the traversal, and it is the only one.</b> Two rules decide what the
///         accessibility tree is, and they used to live in a test-support assembly:
///     </para>
///     <para>
///         ⚠ <b>An element that is not a node is walked <i>through</i> rather than skipped.</b> Its
///         children rise to its parent's depth, which is what ARIA's <c>none</c> means and is the
///         difference between a tree a screen reader can read and thirty nested groups.
///     </para>
///     <para>
///         ⚠ <b>An element reached by <see cref="AccessibleRelation.Owns" /> is emitted under its
///         owner and not where the element tree has it.</b> A <c>Select</c>'s list is a child of the
///         document root — an overlay inside the field that opens it would be clipped — and the
///         relation is how the control says so. Reproducing that wrongly gives a screen reader a
///         combo box with nothing in it and a pile of loose lists at the end.
///     </para>
///     <para>
///         <b><c>AccessibilitySnapshot.Render</c> is a renderer over this and no longer a second
///         walk</b>, which is what makes this repository's accessibility test discipline a bridge's
///         evidence rather than a parallel universe: the string a control's snapshot test asserts on
///         is rendered from the same nodes a screen reader would be handed.
///     </para>
/// </remarks>
public sealed class AccessibilityTree {
    readonly Dictionary<int, AccessibilityNode> byId;

    internal AccessibilityTree(
        IReadOnlyList<AccessibilityNode> roots,
        IReadOnlyList<AccessibilityNode> ordered,
        Dictionary<int, AccessibilityNode> byId
    ) {
        Roots = roots;
        Nodes = ordered;
        this.byId = byId;
    }

    /// <summary>The tops of the tree.</summary>
    /// <remarks>
    ///     More than one is normal rather than exceptional: a document root with no role of its own is
    ///     walked through, so its children are the roots.
    /// </remarks>
    public IReadOnlyList<AccessibilityNode> Roots { get; }

    /// <summary>How many nodes there are, at every depth.</summary>
    public int Count => byId.Count;

    /// <summary>Every node, in the order the walk reached them, which is document order.</summary>
    /// <remarks>
    ///     ⚠ <b>A list rather than the id map's values, because the order is part of the contract.</b>
    ///     <see cref="Diff" /> promises a parent before its children and a dictionary promises nothing
    ///     about enumeration order at all — a promise that happens to hold today is the kind that
    ///     breaks a bridge on a runtime upgrade rather than on a change to this file.
    /// </remarks>
    public IReadOnlyList<AccessibilityNode> Nodes { get; }

    /// <summary>Captures the accessibility tree under an element.</summary>
    /// <param name="root">Where to start — usually <c>document.Root</c>.</param>
    /// <returns>The capture, with ids assigned fresh.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Fresh ids, so two captures made this way cannot be diffed.</b> A bridge holds an
    ///     <see cref="AccessibilityTreeSource" />, which is what makes an id mean the same node from
    ///     one frame to the next. This overload is for the caller that wants one look — a test, a
    ///     dump, an assertion.
    /// </remarks>
    public static AccessibilityTree Capture(UiElement root) => new AccessibilityTreeSource().Capture(root);

    /// <summary>What changed between two captures.</summary>
    /// <param name="previous">The earlier capture, or <c>null</c> for the first one.</param>
    /// <param name="current">The later capture.</param>
    /// <returns>One entry per node that was added, removed or edited. Empty when nothing differs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The thing <see cref="UiDocument.AccessibilityInvalidated" /> deliberately does not
    ///         carry.</b> That event coalesces to one raise a frame with no payload, for a reason its
    ///         own remarks give — accumulating a changed set per mutation is the allocation it exists
    ///         to avoid. Every bridge wants the opposite shape: AT-SPI needs a signal per mutation,
    ///         UIA needs <c>UiaRaiseAutomationEvent</c> per change, <c>NSAccessibility</c> posts
    ///         notifications. This is where "something changed" becomes "these nodes, this way", once,
    ///         rather than three times in three bridges with three answers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is defined and a bridge may rely on it</b>: additions and edits in the
    ///         current capture's document order, then removals in the previous capture's. A parent is
    ///         therefore always announced before its new children, which is what
    ///         <c>NSAccessibilityCreatedNotification</c> and AT-SPI's <c>children-changed:add</c> both
    ///         assume, and a removal is announced after everything that could have replaced it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A first capture is every node added, not an empty diff.</b> <c>null</c> means "a
    ///         bridge that has just attached", and telling it nothing changed would leave it with an
    ///         empty tree that agrees with itself — which is the vacuous pass this whole file is
    ///         arranged to avoid.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<AccessibilityChange> Diff(AccessibilityTree? previous, AccessibilityTree current) {
        ArgumentNullException.ThrowIfNull(current);

        var changes = new List<AccessibilityChange>();

        foreach (var node in current.Nodes) {
            if (previous?.byId.TryGetValue(node.Id, out var was) is not true) {
                changes.Add(new(AccessibilityChangeKind.Added, node.Id, AccessibilityFields.None));

                continue;
            }

            var fields = Differences(was, node);

            if (fields != AccessibilityFields.None) {
                changes.Add(new(AccessibilityChangeKind.Changed, node.Id, fields));
            }
        }

        if (previous is not null) {
            foreach (var node in previous.Nodes) {
                if (!current.byId.ContainsKey(node.Id)) {
                    changes.Add(new(AccessibilityChangeKind.Removed, node.Id, AccessibilityFields.None));
                }
            }
        }

        return changes;
    }

    /// <summary>The node with an id, if this capture has one.</summary>
    /// <param name="id">The node id.</param>
    /// <returns>The node, or <c>null</c>.</returns>
    public AccessibilityNode? Node(int id) => byId.GetValueOrDefault(id);

    static AccessibilityFields Differences(AccessibilityNode was, AccessibilityNode now) {
        var fields = AccessibilityFields.None;

        if (was.Role != now.Role) {
            fields |= AccessibilityFields.Role;
        }

        if (!string.Equals(was.Name, now.Name, StringComparison.Ordinal)) {
            fields |= AccessibilityFields.Name;
        }

        if (!string.Equals(was.Description, now.Description, StringComparison.Ordinal)) {
            fields |= AccessibilityFields.Description;
        }

        if (!string.Equals(was.Value, now.Value, StringComparison.Ordinal)) {
            fields |= AccessibilityFields.Value;
        }

        if (was.States != now.States) {
            fields |= AccessibilityFields.States;
        }

        if (was.Bounds != now.Bounds) {
            fields |= AccessibilityFields.Bounds;
        }

        if (was.Parent != now.Parent) {
            fields |= AccessibilityFields.Parent;
        }

        if (!SameLinks(was.Links, now.Links)) {
            fields |= AccessibilityFields.Links;
        }

        if (!SameChildren(was.Children, now.Children)) {
            fields |= AccessibilityFields.Children;
        }

        return fields;
    }

    static bool SameLinks(IReadOnlyList<AccessibilityLink> was, IReadOnlyList<AccessibilityLink> now) {
        if (was.Count != now.Count) {
            return false;
        }

        for (var i = 0; i < was.Count; i++) {
            if (was[i] != now[i]) {
                return false;
            }
        }

        return true;
    }

    static bool SameChildren(IReadOnlyList<AccessibilityNode> was, IReadOnlyList<AccessibilityNode> now) {
        if (was.Count != now.Count) {
            return false;
        }

        for (var i = 0; i < was.Count; i++) {
            // ⚠ By id and in order. A reordered list of the same children is a change every bridge
            // has to be told about — a screen reader reads siblings in the order it was given them.
            if (was[i].Id != now[i].Id) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Captures the accessibility tree, frame after frame, with node ids that stay put.</summary>
/// <remarks>
///     <para>
///         <b>The mutable half, deliberately separated from the immutable one.</b> An
///         <see cref="AccessibilityTree" /> is a frame that has already happened and can be read from
///         any thread; this is the map from live elements to node ids, and it belongs to the thread
///         that owns the document, like everything else in <c>Vixen.Ui</c>.
///     </para>
///     <para>
///         ⚠ <b>An id is never handed out twice.</b> A removed element's id is retired rather than
///         recycled, so a bridge holding a stale reference to node 41 gets nothing back rather than
///         someone else's button. That is what makes the platform objects a bridge keeps —
///         <c>AXUIElement</c>s, UIA providers — safe to key on the id.
///     </para>
///     <para>
///         Usage is one call from <see cref="UiDocument.AccessibilityInvalidated" />:
///         <see cref="Capture" /> for the new tree, and <see cref="Changes" /> for what to post.
///     </para>
/// </remarks>
public sealed class AccessibilityTreeSource {
    readonly Dictionary<UiElement, int> ids = [];
    int next = 1;

    /// <summary>The most recent capture, or <c>null</c> before the first one.</summary>
    public AccessibilityTree? Tree { get; private set; }

    /// <summary>What the most recent <see cref="Capture" /> changed, against the one before it.</summary>
    /// <remarks>
    ///     Empty before the first capture. The first capture itself is every node added — see
    ///     <see cref="AccessibilityTree.Diff" /> for why that rather than an empty list.
    /// </remarks>
    public IReadOnlyList<AccessibilityChange> Changes { get; private set; } = [];

    /// <summary>Captures the accessibility tree under an element, and diffs it against the last one.</summary>
    /// <param name="root">Where to start — usually <c>document.Root</c>.</param>
    /// <returns>The capture, which is also left in <see cref="Tree" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root" /> is null.</exception>
    public AccessibilityTree Capture(UiElement root) {
        ArgumentNullException.ThrowIfNull(root);

        var owned = new HashSet<UiElement>();
        CollectOwned(root, owned, []);

        var byId = new Dictionary<int, AccessibilityNode>();
        var taken = new Dictionary<UiElement, AccessibilityNode>();
        var ordered = new List<AccessibilityNode>();
        var visited = new HashSet<UiElement>();
        var roots = new List<AccessibilityNode>();

        Walk(root, owned, visited, byId, taken, ordered, roots);

        foreach (var (element, node) in taken) {
            node.Links = Links(element, taken);
        }

        // ⚠ Elements that have left the tree are dropped from the id map here and not when they were
        // detached, because nothing tells this object about a detachment. The map is therefore a
        // strong reference to every element of the last capture — which is fine, they are the ones
        // still on screen, and one capture is what it takes for a removed subtree to be let go.
        if (ids.Count != taken.Count) {
            foreach (var element in ids.Keys.ToArray()) {
                if (!taken.ContainsKey(element)) {
                    ids.Remove(element);
                }
            }
        }

        var tree = new AccessibilityTree(roots, ordered, byId);

        Changes = AccessibilityTree.Diff(Tree, tree);
        Tree = tree;

        return tree;
    }

    /// <summary>Forgets every id, so the next capture is a fresh tree.</summary>
    /// <remarks>For a bridge whose window closed, or a document being swapped under one.</remarks>
    public void Reset() {
        ids.Clear();
        Tree = null;
        Changes = [];
        next = 1;
    }

    static void CollectOwned(UiElement element, HashSet<UiElement> owned, HashSet<UiElement> seen) {
        if (!seen.Add(element)) {
            return;
        }

        foreach (var relationship in element.AccessibleRelationships) {
            if (relationship.Relation == AccessibleRelation.Owns) {
                owned.Add(relationship.Target);
            }
        }

        foreach (var child in element.Children) {
            CollectOwned(child, owned, seen);
        }
    }

    static AccessibilityLink[] Links(UiElement element, Dictionary<UiElement, AccessibilityNode> taken) {
        var relationships = element.AccessibleRelationships;

        if (relationships.Count == 0) {
            return [];
        }

        var links = new AccessibilityLink[relationships.Count];

        for (var i = 0; i < relationships.Count; i++) {
            var relationship = relationships[i];
            var target = taken.GetValueOrDefault(relationship.Target);

            links[i] = new(relationship.Relation, target?.Id ?? 0);
        }

        return links;
    }

    void Walk(
        UiElement element,
        HashSet<UiElement> owned,
        HashSet<UiElement> visited,
        Dictionary<int, AccessibilityNode> byId,
        Dictionary<UiElement, AccessibilityNode> taken,
        List<AccessibilityNode> ordered,
        List<AccessibilityNode> siblings
    ) {
        // ⚠ A guard the string renderer never had, and the walk needs one: `Owns` is declared by hand
        // and two controls owning each other — or one owning its own ancestor — is a stack overflow
        // rather than a diagnostic. It cannot change a well-formed tree's shape, because an owned
        // element is already skipped where the element tree has it.
        if (!visited.Add(element)) {
            return;
        }

        AccessibilityNode? node = null;

        if (element.IsInAccessibilityTree) {
            if (!ids.TryGetValue(element, out var id)) {
                ids[element] = id = next++;
            }

            node = new(id, element);
            byId[id] = node;
            taken[element] = node;
            ordered.Add(node);
            siblings.Add(node);
        }

        var children = node is null ? siblings : new List<AccessibilityNode>();

        foreach (var child in element.Children) {
            // Walked under whoever owns it, further up or further down. Reaching it here as well
            // would put a `Select`'s list in the tree twice.
            if (!owned.Contains(child)) {
                Walk(child, owned, visited, byId, taken, ordered, children);
            }
        }

        foreach (var relationship in element.AccessibleRelationships) {
            if (relationship.Relation == AccessibleRelation.Owns) {
                Walk(relationship.Target, owned, visited, byId, taken, ordered, children);
            }
        }

        if (node is not null) {
            node.Children = children;

            foreach (var child in children) {
                child.Parent = node.Id;
            }
        }
    }
}
