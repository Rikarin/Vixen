// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>Putting a child back where it was, which is the half of undo the list could not do.</summary>
/// <remarks>
///     Linking prepends, which is right for building a hierarchy and wrong for restoring one. These
///     are about the difference: a user who moves the third of five children and presses Ctrl+Z has
///     not undone anything if it comes back first.
/// </remarks>
public sealed class SiblingOrderTests {
    static (World World, Entity Parent, Entity[] Children) Family(int count) {
        var world = new World();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var children = new Entity[count];

        // Backwards, because linking prepends — so the list reads first, second, third.
        for (var index = count - 1; index >= 0; index--) {
            children[index] = Hierarchy.CreateTransform(world, LocalTransform.Identity);
            Hierarchy.SetParent(world, children[index], parent);
        }

        return (world, parent, children);
    }

    static List<Entity> Children(World world, Entity entity) {
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    [Fact]
    public void The_fixture_builds_the_order_it_claims() {
        var (world, parent, children) = Family(3);
        using var owned = world;

        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_child_goes_back_behind_the_one_it_was_behind() {
        var (world, parent, children) = Family(5);
        using var owned = world;

        var moved = children[2];
        var after = Hierarchy.PreviousSiblingOf(world, moved);

        Assert.Equal(children[1], after);

        Hierarchy.SetParent(world, moved, Entity.Null);
        Assert.Equal([children[0], children[1], children[3], children[4]], Children(world, parent));

        Hierarchy.SetParentAfter(world, moved, parent, after);
        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_child_that_was_first_goes_back_first() {
        var (world, parent, children) = Family(3);
        using var owned = world;

        var moved = children[0];

        Assert.True(Hierarchy.PreviousSiblingOf(world, moved).IsNull);

        Hierarchy.SetParent(world, moved, Entity.Null);
        Hierarchy.SetParentAfter(world, moved, parent, Entity.Null);

        Assert.Equal(children, Children(world, parent));
    }

    [Fact]
    public void A_child_that_was_last_goes_back_last() {
        var (world, parent, children) = Family(4);
        using var owned = world;

        var moved = children[3];
        var after = Hierarchy.PreviousSiblingOf(world, moved);

        Hierarchy.SetParent(world, moved, Entity.Null);
        Hierarchy.SetParentAfter(world, moved, parent, after);

        Assert.Equal(children, Children(world, parent));
        Assert.True(world.Read<Sibling>(moved).Next.IsNull);
    }

    [Fact]
    public void The_links_point_both_ways_after_a_splice() {
        var (world, parent, children) = Family(4);
        using var owned = world;

        var moved = children[1];

        Hierarchy.SetParent(world, moved, Entity.Null);
        Hierarchy.SetParentAfter(world, moved, parent, children[0]);

        // A forward walk is what `ChildrenOf` does, so a broken `Previous` is invisible to it — and
        // it is what `Unlink` reads, so the next removal would corrupt the list instead of failing.
        Assert.Equal(children[0], world.Read<Sibling>(moved).Previous);
        Assert.Equal(children[2], world.Read<Sibling>(moved).Next);
        Assert.Equal(moved, world.Read<Sibling>(children[2]).Previous);
        Assert.Equal(moved, world.Read<Sibling>(children[0]).Next);
    }

    [Fact]
    public void An_entity_from_elsewhere_can_be_spliced_into_a_position() {
        var (world, parent, children) = Family(3);
        using var owned = world;

        var stranger = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParentAfter(world, stranger, parent, children[0]);

        Assert.Equal([children[0], stranger, children[1], children[2]], Children(world, parent));
        Assert.Equal(1, Hierarchy.DepthOf(world, stranger));
    }

    [Fact]
    public void Moving_a_child_within_its_own_parent_does_not_lose_it() {
        var (world, parent, children) = Family(4);
        using var owned = world;

        // Already a child of this parent, so `SetParent` returns early — and the splice still has to
        // happen. A version that trusted `SetParent` to do the linking would silently do nothing.
        Hierarchy.SetParentAfter(world, children[0], parent, children[2]);

        Assert.Equal([children[1], children[2], children[0], children[3]], Children(world, parent));
    }

    [Fact]
    public void A_neighbour_that_is_not_a_child_of_the_parent_is_refused() {
        var (world, parent, children) = Family(2);
        using var owned = world;

        var stranger = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        // A recorded position whose neighbour has since moved away. Silently becoming "first" is the
        // failure this refuses: the caller is the one that can tell whether that is acceptable.
        Assert.Throws<InvalidOperationException>(
            () => Hierarchy.SetParentAfter(world, children[0], parent, stranger)
        );
    }

    [Fact]
    public void A_root_has_no_order_to_restore_and_says_so() {
        var (world, _, children) = Family(1);
        using var owned = world;

        Assert.Throws<InvalidOperationException>(
            () => Hierarchy.SetParentAfter(world, children[0], Entity.Null, Entity.Null)
        );
    }

    [Fact]
    public void A_cycle_is_refused_the_way_SetParent_refuses_it() {
        var (world, parent, children) = Family(2);
        using var owned = world;

        Assert.Throws<InvalidOperationException>(
            () => Hierarchy.SetParentAfter(world, parent, children[0], Entity.Null)
        );
    }

    [Fact]
    public void Every_position_in_a_list_round_trips() {
        for (var index = 0; index < 5; index++) {
            var (world, parent, children) = Family(5);
            using var owned = world;

            var moved = children[index];
            var after = Hierarchy.PreviousSiblingOf(world, moved);

            Hierarchy.SetParent(world, moved, Entity.Null);
            Hierarchy.SetParentAfter(world, moved, parent, after);

            Assert.Equal(children, Children(world, parent));
        }
    }
}
