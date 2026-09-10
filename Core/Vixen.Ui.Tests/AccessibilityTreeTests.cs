// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The accessibility tree as data, and the per-node change list a bridge posts from.</summary>
/// <remarks>
///     <para>
///         <b>The two shape rules are the whole of it</b>, and until this file they were implemented
///         once, in <c>Vixen.Ui.Testing</c>, as a string: an element that is not a node is walked
///         <i>through</i>, and an element reached by <see cref="AccessibleRelation.Owns" /> is emitted
///         under its owner. A platform bridge that reproduced either wrongly gives a screen reader
///         thirty nested groups and a pile of loose lists, so both are asserted here on the data
///         rather than inferred from indentation.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day it does not run.</b> A capture of an empty document is a
///         tree with no nodes, and a diff of that against itself is an empty change list — which is
///         the shape of every assertion here passing vacuously. So every test that asserts a diff is
///         empty is paired with one that asserts the same arrangement produces a specific non-empty
///         one, on <c>CommandInvalidationTests</c>' terms.
///     </para>
/// </remarks>
public class AccessibilityTreeTests {
    static UiElement Node(UiElement parent, AccessibleRole role, string name) {
        var element = parent.Add("div");

        element.Role = role;
        element.AccessibleName = name;

        return element;
    }

    static string[] Names(IEnumerable<AccessibilityNode> nodes) => nodes.Select(node => node.Name!).ToArray();

    [Fact]
    public void An_element_with_no_role_is_walked_through_rather_than_skipped() {
        using var document = new UiDocument(100f, 100f);

        var wrapper = document.Root.Add("div");
        var inner = wrapper.Add("div");

        Node(inner, AccessibleRole.Button, "press me");

        var tree = AccessibilityTree.Capture(document.Root);

        // Two wrappers and a document root deep in the element tree; one node, at the top, in this one.
        Assert.Equal(1, tree.Count);
        Assert.Equal(["press me"], Names(tree.Roots));
        Assert.Equal(0, tree.Roots[0].Parent);
    }

    [Fact]
    public void Depth_follows_the_nodes_and_not_the_elements() {
        using var document = new UiDocument(100f, 100f);

        var group = Node(document.Root, AccessibleRole.Group, "toolbar");
        var wrapper = group.Add("div");

        Node(wrapper, AccessibleRole.Button, "cut");
        Node(wrapper, AccessibleRole.Button, "paste");

        var tree = AccessibilityTree.Capture(document.Root);

        Assert.Equal(["toolbar"], Names(tree.Roots));
        Assert.Equal(["cut", "paste"], Names(tree.Roots[0].Children));
        Assert.All(tree.Roots[0].Children, child => Assert.Equal(tree.Roots[0].Id, child.Parent));
    }

    [Fact]
    public void An_owned_element_is_emitted_under_its_owner_and_only_there() {
        using var document = new UiDocument(100f, 100f);

        var combo = Node(document.Root, AccessibleRole.ComboBox, "country");
        var list = Node(document.Root, AccessibleRole.ListBox, "countries");

        Node(list, AccessibleRole.Option, "Slovakia");
        combo.AddAccessibleRelation(AccessibleRelation.Owns, list);

        var tree = AccessibilityTree.Capture(document.Root);

        // The element tree has the list beside the combo box, because a popover inside the field
        // would be clipped. The accessibility tree has to have it inside.
        Assert.Equal(["country"], Names(tree.Roots));
        Assert.Equal(["countries"], Names(tree.Roots[0].Children));
        Assert.Equal(["Slovakia"], Names(tree.Roots[0].Children[0].Children));
        Assert.Equal(3, tree.Count);
        Assert.Equal(tree.Roots[0].Id, tree.Roots[0].Children[0].Parent);
    }

    /// <summary>The same rule, with the owned element earlier in the element tree than its owner.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the ordering that actually tests the rule, and the other one does not.</b>
    ///     When the owner comes first the walk reaches the owned element through the relation before
    ///     it reaches it as a sibling, so the cycle guard alone puts it in the right place and a
    ///     capture that had dropped the "skip it where the element tree has it" rule entirely would
    ///     still pass — measured, by deleting that line and watching the suite stay green. With the
    ///     owned element first, only the rule saves it: the guard would pin it at the document root
    ///     and leave the combo box empty, which is the picture <c>Owns</c> exists to correct.
    /// </remarks>
    [Fact]
    public void An_owned_element_that_comes_first_still_lands_under_its_owner() {
        using var document = new UiDocument(100f, 100f);

        var list = Node(document.Root, AccessibleRole.ListBox, "countries");
        var combo = Node(document.Root, AccessibleRole.ComboBox, "country");

        Node(list, AccessibleRole.Option, "Slovakia");
        combo.AddAccessibleRelation(AccessibleRelation.Owns, list);

        var tree = AccessibilityTree.Capture(document.Root);

        Assert.Equal(["country"], Names(tree.Roots));
        Assert.Equal(["countries"], Names(tree.Roots[0].Children));
        Assert.Equal(["Slovakia"], Names(tree.Roots[0].Children[0].Children));
    }

    [Fact]
    public void A_relation_resolves_to_a_node_id_and_to_zero_when_the_target_is_not_a_node() {
        using var document = new UiDocument(100f, 100f);

        var field = Node(document.Root, AccessibleRole.TextBox, "email");
        var panel = Node(document.Root, AccessibleRole.Group, "settings");
        var invisible = document.Root.Add("div");

        field.AddAccessibleRelation(AccessibleRelation.Controls, panel);
        field.AddAccessibleRelation(AccessibleRelation.DescribedBy, invisible);

        var tree = AccessibilityTree.Capture(document.Root);
        var links = tree.Roots[0].Links;

        Assert.Equal(2, links.Count);
        Assert.Equal(new(AccessibleRelation.Controls, tree.Roots[1].Id), links[0]);

        // ⚠ Zero rather than an omission: the relation was declared, and a bridge that saw a shorter
        // list would have no way to tell a target it cannot reach from a relation nobody added.
        Assert.Equal(new(AccessibleRelation.DescribedBy, 0), links[1]);
    }

    /// <summary>Two elements owning each other, captured from one of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The capture has to start at an owned element for a cycle to be reachable at all</b>,
    ///     and that is not an exotic call — a bridge captures a subtree, and a test captures the
    ///     control under test. Everywhere else the two rules cancel: an element reached by
    ///     <see cref="AccessibleRelation.Owns" /> is skipped where the element tree has it, so a cycle
    ///     among elements that are all owned simply makes the whole group unreachable from the
    ///     document root rather than recursive. From inside it, the walk goes round for ever without
    ///     the visited set, and <c>Owns</c> is declared by hand with nothing validating it.
    /// </remarks>
    [Fact]
    public void A_cycle_in_owns_terminates() {
        using var document = new UiDocument(100f, 100f);

        var first = Node(document.Root, AccessibleRole.Group, "first");
        var second = Node(first, AccessibleRole.Group, "second");

        first.AddAccessibleRelation(AccessibleRelation.Owns, second);
        second.AddAccessibleRelation(AccessibleRelation.Owns, first);

        // The assertion is that this returns at all, and with each node once.
        var tree = AccessibilityTree.Capture(first);

        Assert.Equal(2, tree.Count);
        Assert.Equal(["first"], Names(tree.Roots));
        Assert.Equal(["second"], Names(tree.Roots[0].Children));
    }

    [Fact]
    public void An_id_is_stable_across_captures_and_is_never_handed_out_twice() {
        using var document = new UiDocument(100f, 100f);

        var source = new AccessibilityTreeSource();
        var kept = Node(document.Root, AccessibleRole.Button, "kept");
        var going = Node(document.Root, AccessibleRole.Button, "going");

        var first = source.Capture(document.Root);
        var keptId = first.Roots[0].Id;
        var goingId = first.Roots[1].Id;

        kept.AccessibleName = "kept, renamed";
        going.Remove();

        var second = source.Capture(document.Root);

        Assert.Equal(keptId, second.Roots[0].Id);
        Assert.Null(second.Node(goingId));

        Node(document.Root, AccessibleRole.Button, "new");

        var third = source.Capture(document.Root);

        // ⚠ The retired id is not recycled. A bridge keys its platform objects — AXUIElements, UIA
        // providers — on this number, and handing 2 to a different button would hand a screen reader
        // the wrong element rather than nothing.
        Assert.Equal(keptId, third.Roots[0].Id);
        Assert.NotEqual(goingId, third.Roots[1].Id);
        Assert.Equal("new", third.Roots[1].Name);
    }

    [Fact]
    public void A_first_capture_is_every_node_added() {
        using var document = new UiDocument(100f, 100f);

        var group = Node(document.Root, AccessibleRole.Group, "group");
        Node(group, AccessibleRole.Button, "button");

        var source = new AccessibilityTreeSource();

        Assert.Empty(source.Changes);

        var tree = source.Capture(document.Root);

        Assert.Equal(2, source.Changes.Count);
        Assert.All(source.Changes, change => Assert.Equal(AccessibilityChangeKind.Added, change.Kind));

        // ⚠ The parent first. `NSAccessibilityCreatedNotification` and AT-SPI's
        // `children-changed:add` both assume the container exists by the time a child is announced.
        Assert.Equal(tree.Roots[0].Id, source.Changes[0].Node);
        Assert.Equal(tree.Roots[0].Children[0].Id, source.Changes[1].Node);
    }

    [Fact]
    public void A_capture_that_changed_nothing_reports_nothing() {
        using var document = new UiDocument(100f, 100f);

        Node(document.Root, AccessibleRole.Button, "button");

        var source = new AccessibilityTreeSource();

        source.Capture(document.Root);
        source.Capture(document.Root);

        // Half of a pair: the other tests are what stop this being satisfied by a walk that found
        // nothing in the first place.
        Assert.Empty(source.Changes);
    }

    [Fact]
    public void A_renamed_node_reports_its_name_and_nothing_else() {
        using var document = new UiDocument(100f, 100f);

        var button = Node(document.Root, AccessibleRole.Button, "save");
        var source = new AccessibilityTreeSource();
        var tree = source.Capture(document.Root);

        button.AccessibleName = "save as";
        source.Capture(document.Root);

        var change = Assert.Single(source.Changes);

        Assert.Equal(AccessibilityChangeKind.Changed, change.Kind);
        Assert.Equal(tree.Roots[0].Id, change.Node);
        Assert.Equal(AccessibilityFields.Name, change.Fields);
    }

    [Fact]
    public void A_state_change_reports_states() {
        using var document = new UiDocument(100f, 100f);

        var box = Node(document.Root, AccessibleRole.CheckBox, "remember me");
        var source = new AccessibilityTreeSource();

        source.Capture(document.Root);
        box.DeclaredAccessibleState = AccessibleStates.Required;
        source.Capture(document.Root);

        var change = Assert.Single(source.Changes);

        Assert.Equal(AccessibilityFields.States, change.Fields);
    }

    [Fact]
    public void Adding_a_child_reports_the_child_added_and_the_parent_changed() {
        using var document = new UiDocument(100f, 100f);

        var group = Node(document.Root, AccessibleRole.Group, "group");
        var source = new AccessibilityTreeSource();
        var first = source.Capture(document.Root);

        Node(group, AccessibleRole.Button, "late");

        var second = source.Capture(document.Root);

        Assert.Equal(2, source.Changes.Count);
        Assert.Equal(
            new(AccessibilityChangeKind.Changed, first.Roots[0].Id, AccessibilityFields.Children),
            source.Changes[0]
        );

        Assert.Equal(
            new(AccessibilityChangeKind.Added, second.Roots[0].Children[0].Id, AccessibilityFields.None),
            source.Changes[1]
        );
    }

    [Fact]
    public void A_removal_is_reported_after_everything_else() {
        using var document = new UiDocument(100f, 100f);

        var group = Node(document.Root, AccessibleRole.Group, "group");
        var doomed = Node(group, AccessibleRole.Button, "doomed");

        var source = new AccessibilityTreeSource();
        var first = source.Capture(document.Root);
        var doomedId = first.Roots[0].Children[0].Id;

        doomed.Remove();
        source.Capture(document.Root);

        Assert.Equal(2, source.Changes.Count);
        Assert.Equal(AccessibilityChangeKind.Changed, source.Changes[0].Kind);
        Assert.Equal(new(AccessibilityChangeKind.Removed, doomedId, AccessibilityFields.None), source.Changes[1]);
    }

    [Fact]
    public void Reordering_siblings_is_a_change_to_the_parent() {
        using var document = new UiDocument(100f, 100f);

        var group = Node(document.Root, AccessibleRole.Group, "group");
        var first = Node(group, AccessibleRole.Button, "first");

        Node(group, AccessibleRole.Button, "second");

        var source = new AccessibilityTreeSource();

        source.Capture(document.Root);

        // Moving the first child to the end. A screen reader reads siblings in the order it was
        // handed them, so this is a change even though every node's own properties are identical.
        document.Reparent(first, group);

        source.Capture(document.Root);

        var change = Assert.Single(source.Changes);

        Assert.Equal(AccessibilityFields.Children, change.Fields);
        Assert.Equal(["second", "first"], Names(source.Tree!.Roots[0].Children));
    }

    [Fact]
    public void Reset_forgets_every_id() {
        using var document = new UiDocument(100f, 100f);

        Node(document.Root, AccessibleRole.Button, "button");

        var source = new AccessibilityTreeSource();

        source.Capture(document.Root);
        source.Reset();

        Assert.Null(source.Tree);
        Assert.Empty(source.Changes);

        var tree = source.Capture(document.Root);

        Assert.Equal(1, tree.Roots[0].Id);
        Assert.Single(source.Changes);
    }

    [Fact]
    public void A_capture_carries_the_bounds_the_element_was_laid_out_at() {
        using var document = new UiDocument(100f, 100f);

        var button = Node(document.Root, AccessibleRole.Button, "button");

        document.Update();

        var node = AccessibilityTree.Capture(document.Root).Roots[0];

        Assert.Equal(button.Bounds, node.Bounds);
    }

    [Fact]
    public void Capture_rejects_a_null_root() =>
        Assert.Throws<ArgumentNullException>(() => AccessibilityTree.Capture(null!));
}
