// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests;

/// <summary>
///     The store, rather than the algorithm: what a node is, where its children live, and what a
///     style write costs. The algorithm is judged by the ported Yoga suite in <c>Generated/</c>.
/// </summary>
public class LayoutTreeTests {
    [Fact]
    public void A_new_node_has_the_CSS_initial_style_rather_than_a_zeroed_one() {
        // All-zero is a real style and it is the wrong one: flex-direction would be column by
        // accident rather than by decision, align-items would be auto rather than stretch, and
        // width would be 0 rather than auto.
        using var tree = new LayoutTree();
        var node = tree.CreateNode();

        ref readonly var style = ref tree.GetStyle(node);

        Assert.Equal(FlexDirection.Column, style.FlexDirection);
        Assert.Equal(Align.Stretch, style.AlignItems);
        Assert.Equal(Align.Auto, style.AlignSelf);
        Assert.Equal(PositionType.Relative, style.PositionType);
        Assert.Equal(LayoutUnit.Auto, style.Dimensions[(int) Dimension.Width].Unit);
        Assert.Equal(LayoutUnit.Auto, style.FlexBasis.Unit);
        Assert.True(float.IsNaN(style.FlexGrow));
    }

    [Fact]
    public void Children_keep_the_order_they_were_inserted_in() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var first = tree.CreateNode();
        var second = tree.CreateNode();
        var third = tree.CreateNode();

        tree.AddChild(root, first);
        tree.AddChild(root, third);
        tree.InsertChild(root, second, 1);

        Assert.Equal(3, tree.GetChildCount(root));
        Assert.Equal(first, tree.GetChild(root, 0));
        Assert.Equal(second, tree.GetChild(root, 1));
        Assert.Equal(third, tree.GetChild(root, 2));
        Assert.Equal(root, tree.GetParent(second));
    }

    [Fact]
    public void A_child_list_that_outgrows_its_block_keeps_its_contents() {
        // The block moves into the next size class and the ids are copied. Getting this wrong
        // reads whatever the arena had at that offset, which is another node's children.
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var children = new LayoutNodeId[17];
        for (var i = 0; i < children.Length; i++) {
            children[i] = tree.CreateNode();
            tree.AddChild(root, children[i]);
        }

        for (var i = 0; i < children.Length; i++) {
            Assert.Equal(children[i], tree.GetChild(root, i));
        }
    }

    [Fact]
    public void Two_nodes_cannot_share_a_child() {
        using var tree = new LayoutTree();
        var first = tree.CreateNode();
        var second = tree.CreateNode();
        var child = tree.CreateNode();

        tree.AddChild(first, child);

        Assert.Throws<InvalidOperationException>(() => tree.AddChild(second, child));
    }

    [Fact]
    public void Removing_a_child_detaches_it_and_leaves_the_rest_in_order() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var first = tree.CreateNode();
        var second = tree.CreateNode();
        var third = tree.CreateNode();
        tree.AddChild(root, first);
        tree.AddChild(root, second);
        tree.AddChild(root, third);

        Assert.True(tree.RemoveChild(root, second));
        Assert.False(tree.RemoveChild(root, second));

        Assert.Equal(2, tree.GetChildCount(root));
        Assert.Equal(first, tree.GetChild(root, 0));
        Assert.Equal(third, tree.GetChild(root, 1));
        Assert.False(tree.GetParent(second).IsValid);
    }

    [Fact]
    public void Destroying_a_subtree_frees_its_slots_for_reuse() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var child = tree.CreateNode();
        var grandchild = tree.CreateNode();
        tree.AddChild(root, child);
        tree.AddChild(child, grandchild);

        Assert.Equal(3, tree.NodeCount);

        tree.DestroyRecursive(root);

        Assert.Equal(0, tree.NodeCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetChildCount(root));
    }

    [Fact]
    public void Setting_a_style_marks_the_node_and_its_ancestors() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var child = tree.CreateNode();
        var grandchild = tree.CreateNode();
        tree.AddChild(root, child);
        tree.AddChild(child, grandchild);

        ClearDirty(tree, root, child, grandchild);

        tree.SetDimension(grandchild, Dimension.Width, StyleLength.Points(10f));

        Assert.True(tree.IsDirty(grandchild));
        Assert.True(tree.IsDirty(child));
        Assert.True(tree.IsDirty(root));
    }

    [Fact]
    public void Setting_a_style_to_what_it_already_was_marks_nothing() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        var child = tree.CreateNode();
        tree.AddChild(root, child);
        tree.SetDimension(child, Dimension.Width, StyleLength.Points(10f));

        ClearDirty(tree, root, child);

        tree.SetDimension(child, Dimension.Width, StyleLength.Points(10f));

        Assert.False(tree.IsDirty(child));
        Assert.False(tree.IsDirty(root));
    }

    [Fact]
    public void An_edge_shorthand_and_a_specific_edge_are_kept_apart() {
        // `padding: 5` then `padding-left: 9` is not the same document as the reverse, and a store
        // that expanded shorthands on the way in could not tell them apart.
        using var tree = new LayoutTree();
        var node = tree.CreateNode();

        tree.SetPadding(node, Edge.All, StyleLength.Points(5f));
        tree.SetPadding(node, Edge.Left, StyleLength.Points(9f));

        ref readonly var style = ref tree.GetStyle(node);

        Assert.Equal(5f, style.Padding[(int) Edge.All].Value);
        Assert.Equal(9f, style.Padding[(int) Edge.Left].Value);
        Assert.Equal(9f, StyleResolution.EdgeValue(in style.Padding, Edge.Left, Direction.Ltr).Value);
        Assert.Equal(5f, StyleResolution.EdgeValue(in style.Padding, Edge.Right, Direction.Ltr).Value);
    }

    [Fact]
    public void The_writing_direction_decides_which_edge_start_means() {
        using var tree = new LayoutTree();
        var node = tree.CreateNode();
        tree.SetMargin(node, Edge.Start, StyleLength.Points(7f));

        ref readonly var style = ref tree.GetStyle(node);

        Assert.Equal(7f, StyleResolution.EdgeValue(in style.Margin, Edge.Left, Direction.Ltr).Value);
        Assert.False(StyleResolution.EdgeValue(in style.Margin, Edge.Right, Direction.Ltr).IsDefined);
        Assert.Equal(7f, StyleResolution.EdgeValue(in style.Margin, Edge.Right, Direction.Rtl).Value);
        Assert.False(StyleResolution.EdgeValue(in style.Margin, Edge.Left, Direction.Rtl).IsDefined);
    }

    [Fact]
    public void A_node_with_children_cannot_also_measure_itself() {
        using var tree = new LayoutTree();
        var root = tree.CreateNode();
        tree.AddChild(root, tree.CreateNode());

        Assert.Throws<InvalidOperationException>(() =>
            tree.SetMeasureFunction(root, static (in MeasureRequest _) => new LayoutSize(0f, 0f))
        );
    }

    [Fact]
    public void A_node_with_no_measure_function_cannot_be_dirtied_by_hand() {
        using var tree = new LayoutTree();
        var node = tree.CreateNode();

        Assert.Throws<InvalidOperationException>(() => tree.MarkDirty(node));
    }

    [Fact]
    public void A_percentage_resolves_against_what_it_is_given() {
        Assert.Equal(25f, StyleLength.Percent(50f).Resolve(50f));
        Assert.Equal(10f, StyleLength.Points(10f).Resolve(999f));
        Assert.True(float.IsNaN(StyleLength.Auto.Resolve(100f)));
        Assert.True(float.IsNaN(StyleLength.Undefined.Resolve(100f)));
    }

    [Fact]
    public void A_length_written_as_NaN_is_undefined_rather_than_a_NaN_length() {
        // Otherwise it resolves to NaN, which propagates into a size, and the assignment that
        // caused it is nowhere near where it is noticed.
        var length = new StyleLength(float.NaN, LayoutUnit.Point);

        Assert.False(length.IsDefined);
        Assert.Equal(LayoutUnit.Undefined, length.Unit);
    }

    static void ClearDirty(LayoutTree tree, params LayoutNodeId[] nodes) {
        foreach (var node in nodes) {
            tree.MarkClean(node);
        }
    }
}
