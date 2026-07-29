// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Two gestures a docking host has to get right, and got wrong.</summary>
/// <remarks>
///     ⚠ <b>Both are things users do by accident and then report as docking being broken.</b>
///     Dropping a tab where it already is is the commonest gesture in a docking host — pick it up,
///     change your mind, let go — and dragging a tab two places along a strip is how anybody expects
///     to reorder one.
/// </remarks>
public class DockReorderTests {
    static DockLayout Stack(params string[] panels) => new() { Root = new DockGroupNode(panels) };

    static List<string> Order(DockLayout layout) => [.. layout.Groups().First().Panels];

    [Fact]
    public void Dropping_a_tab_into_its_own_solitary_group_does_nothing() {
        var layout = new DockLayout {
            Root = new DockSplitNode(Orientation.Horizontal, new DockGroupNode("scene"), new DockGroupNode("console"))
        };

        var console = layout.Groups().Single(group => group.IndexOf("console") >= 0);

        layout.Dock("console", console, DockZone.Center);

        // ⚠ The failure this stops was not a no-op, it was a *move*: the removal pruned the group
        // out of the tree, `Contains` then failed, and the panel landed in whichever unrelated group
        // survived. Dragging the console into the console put it in the scene.
        Assert.Equal(2, layout.Groups().Count);
        Assert.Equal(["console"], layout.Groups().Single(group => group.IndexOf("console") >= 0).Panels);
    }

    [Theory]
    [InlineData(DockZone.Left)]
    [InlineData(DockZone.Right)]
    [InlineData(DockZone.Top)]
    [InlineData(DockZone.Bottom)]
    public void Splitting_a_solitary_group_against_itself_does_nothing_either(DockZone zone) {
        var layout = new DockLayout {
            Root = new DockSplitNode(Orientation.Horizontal, new DockGroupNode("scene"), new DockGroupNode("console"))
        };

        var console = layout.Groups().Single(group => group.IndexOf("console") >= 0);

        layout.Dock("console", console, zone);

        // There is nothing to split against: the group holds only the panel being moved, so every
        // zone means the arrangement it already has.
        Assert.Equal(2, layout.Groups().Count);
    }

    [Fact]
    public void A_panel_can_still_be_taken_out_of_a_stack_and_docked_beside_it() {
        var layout = Stack("hierarchy", "project");
        var group = layout.Groups().First();

        layout.Dock("project", group, DockZone.Right);

        // The guard is about a group holding *only* the panel being moved. Taking one of two out of
        // a stack is the ordinary, meaningful case and must still work.
        Assert.Equal(2, layout.Groups().Count);
    }

    [Fact]
    public void A_centre_drop_at_an_index_reorders_the_stack() {
        var layout = Stack("hierarchy", "project", "console");
        var group = layout.Groups().First();

        layout.Dock("console", group, DockZone.Center, 0);
        Assert.Equal(["console", "hierarchy", "project"], Order(layout));

        layout.Dock("console", group, DockZone.Center, 2);
        Assert.Equal(["hierarchy", "project", "console"], Order(layout));

        layout.Dock("hierarchy", group, DockZone.Center, 1);
        Assert.Equal(["project", "hierarchy", "console"], Order(layout));
    }

    [Fact]
    public void An_index_past_the_end_lands_at_the_end_rather_than_throwing() {
        var layout = Stack("hierarchy", "project");
        var group = layout.Groups().First();

        // The index was computed from a pointer over a strip that has since had this panel taken out
        // of it, so it can be one past what the group now holds.
        layout.Dock("hierarchy", group, DockZone.Center, 40);

        Assert.Equal(["project", "hierarchy"], Order(layout));
    }

    [Fact]
    public void With_no_index_a_centre_drop_still_appends() {
        var layout = Stack("hierarchy", "project", "console");
        var group = layout.Groups().First();

        // -1 is "you said nothing about order", which a drop over the body rather than the strip
        // means — and appending is the behaviour that was there before an index existed.
        layout.Dock("hierarchy", group, DockZone.Center);

        Assert.Equal(["project", "console", "hierarchy"], Order(layout));
    }

    [Fact]
    public void The_reordered_panel_is_the_one_showing() {
        var layout = Stack("hierarchy", "project", "console");
        var group = layout.Groups().First();

        layout.Dock("console", group, DockZone.Center, 0);

        // Dragging a tab is picking it up, and a drop that put it somewhere and then showed a
        // different one would be a gesture whose result you have to go and find.
        Assert.Equal(0, group.Selected);
    }
}
