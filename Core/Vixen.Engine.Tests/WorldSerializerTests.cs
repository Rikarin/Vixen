// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Engine.Worlds;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>A whole world written down and made again.</summary>
/// <remarks>
///     <para>
///         <b>The oracle is a second capture, not a digest.</b> <c>WorldDigest</c> hashes entity
///         <i>ids</i>, and a restore hands out whatever slots the target world has free — so two
///         worlds in the same state disagree on the digest as soon as the ids do, which they do
///         whenever the capture had more than one archetype in it. Capturing the restored world and
///         comparing the bytes to the first capture asks the question the digest was meant to ask and
///         is blind to the thing that is allowed to differ. It is the same argument the content
///         build's determinism gate makes: build twice, compare bytes.
///     </para>
///     <para>
///         ⚠ <b>Which makes the capture's canonical order load-bearing rather than tidy.</b> If the
///         walk depended on chunk layout, two captures of one state would differ and this file would
///         be green only by luck.
///     </para>
/// </remarks>
public sealed class WorldSerializerTests {
    public WorldSerializerTests() {
        SceneComponentRegistry.Register<Shield>();
        SceneComponentRegistry.Register<Warded>();
    }

    // ---------------------------------------------------------------- the round trip

    [Fact]
    public void A_flat_world_comes_back_with_its_components() {
        using var source = new World();

        source.Create(new Shield { Absorption = 1.5f });
        source.Create(new Shield { Absorption = 2.5f });
        source.Create<Warded>(default);

        var content = WorldSerializer.Capture(source);

        Assert.True(content.IsComplete);
        Assert.Equal(3, content.Count);

        using var target = new World();
        var restored = WorldSerializer.Restore(content, target);

        Assert.Equal(3, target.EntityCount);
        Assert.Equal(
            [1.5f, 2.5f],
            restored.Where(entity => target.Has<Shield>(entity))
                .Select(entity => target.Read<Shield>(entity).Absorption)
                .Order()
        );

        Assert.Single(restored, entity => target.Has<Warded>(entity));
    }

    /// <summary>The gate: capture, restore, capture again, and the two captures are one array.</summary>
    [Fact]
    public void Capturing_the_restored_world_produces_the_same_bytes() {
        using var source = Populated();

        var first = WorldSerializer.Capture(source);

        using var target = new World();
        WorldSerializer.Restore(first, target);

        var second = WorldSerializer.Capture(target);

        Assert.Equal(Serializer.ToBytes(first), Serializer.ToBytes(second));
    }

    /// <summary>And a capture of one state is a capture of one state, however it was walked.</summary>
    /// <remarks>
    ///     ⚠ <b>The two worlds are built in different orders and destroy different entities</b>, so
    ///     their chunks are laid out differently and their free lists are in different states. What is
    ///     the same is what they hold, which is the only thing a capture may depend on. Without this
    ///     the test above would pass for a capture that simply walked chunks.
    /// </remarks>
    [Fact]
    public void Two_worlds_in_one_state_capture_to_the_same_bytes() {
        using var forwards = new World();
        using var backwards = new World();

        var churn = new List<Entity>();

        for (var index = 0; index < 32; index++) {
            churn.Add(backwards.Create(new Shield { Absorption = index }));
        }

        foreach (var entity in churn) {
            backwards.Destroy(entity);
        }

        var left = Tree(forwards);
        var right = Tree(backwards);

        // The ids differ — the second world has thirty-two slots on its free list — and the state
        // does not, which is the whole of what is being asserted.
        Assert.NotEqual(left.Id, right.Id);
        Assert.Equal(
            Serializer.ToBytes(WorldSerializer.Capture(forwards)),
            Serializer.ToBytes(WorldSerializer.Capture(backwards))
        );
    }

    // ---------------------------------------------------------------- the hierarchy

    [Fact]
    public void The_tree_comes_back_with_its_children_in_order() {
        using var source = new World();
        var root = Tree(source);

        var before = Absorptions(source, root);

        Assert.Equal([1f, 2f, 3f], before);

        using var target = new World();
        var restored = WorldSerializer.Restore(WorldSerializer.Capture(source), target);

        var top = restored[0];

        Assert.True(Hierarchy.ParentOf(target, top).IsNull);
        Assert.Equal(before, Absorptions(target, top));
    }

    /// <summary>
    ///     ⚠ <b>The links are never written, so this is not "did the bytes survive" — it is "was the
    ///     hierarchy rebuilt".</b> <c>Parent</c>, <c>Child</c> and <c>Sibling</c> all hold entity
    ///     handles and are excluded from the capture by construction; a grandchild arriving at depth
    ///     two means the parent table and the link pass agree about the shape.
    /// </summary>
    [Fact]
    public void Depth_and_parentage_are_rebuilt_rather_than_stored() {
        using var source = new World();

        var root = Hierarchy.CreateTransform(source, LocalTransform.Identity);
        var middle = Hierarchy.CreateTransform(source, LocalTransform.At(new Vector3(1f, 0f, 0f)));
        var leaf = Hierarchy.CreateTransform(source, LocalTransform.At(new Vector3(0f, 2f, 0f)));

        Hierarchy.SetParent(source, middle, root);
        Hierarchy.SetParent(source, leaf, middle);

        var content = WorldSerializer.Capture(source);

        Assert.Equal([-1, 0, 1], content.Parents);

        using var target = new World();
        var restored = WorldSerializer.Restore(content, target);

        Assert.Equal(0, Hierarchy.DepthOf(target, restored[0]));
        Assert.Equal(1, Hierarchy.DepthOf(target, restored[1]));
        Assert.Equal(2, Hierarchy.DepthOf(target, restored[2]));

        Assert.Equal(restored[0], Hierarchy.ParentOf(target, restored[1]));
        Assert.Equal(restored[1], Hierarchy.ParentOf(target, restored[2]));

        Assert.Equal(new Vector3(0f, 2f, 0f), target.Read<LocalTransform>(restored[2]).Position);
    }

    /// <summary>A root with a depth and no children is the case the link pass never touches.</summary>
    [Fact]
    public void A_lone_transform_keeps_its_hierarchy_depth_component() {
        using var source = new World();
        Hierarchy.CreateTransform(source, LocalTransform.Identity);

        using var target = new World();
        var restored = WorldSerializer.Restore(WorldSerializer.Capture(source), target);

        // Presence, not value: a root's depth is zero either way, and what would be lost by dropping
        // the column is the component — which is a difference in the archetype and so in what matches.
        Assert.True(target.Has<HierarchyDepth>(restored[0]));
        Assert.True(target.Has<WorldTransform>(restored[0]));
    }

    // ---------------------------------------------------------------- what it cannot carry

    /// <summary>
    ///     A runtime handle has no <c>[DataContract]</c> and so no name to be written under. It is
    ///     named in <see cref="WorldContent.Dropped" /> rather than dropped in silence or refused.
    /// </summary>
    [Fact]
    public void A_component_with_no_contract_is_reported() {
        using var source = new World();
        source.Create(new Shield { Absorption = 1f }, new RegistrationTestHandle { Slot = 7 });

        var content = WorldSerializer.Capture(source);

        Assert.False(content.IsComplete);
        Assert.Equal(typeof(RegistrationTestHandle).FullName, Assert.Single(content.Dropped));

        using var target = new World();
        var restored = WorldSerializer.Restore(content, target);

        // What could be carried was carried, and what could not is gone rather than zeroed — an
        // entity holding a slot number that means nothing is worse than one holding no slot at all.
        Assert.Equal(1f, target.Read<Shield>(restored[0]).Absorption);
        Assert.False(target.Has<RegistrationTestHandle>(restored[0]));
    }

    [Fact]
    public void The_dropped_list_names_each_component_once_and_is_sorted() {
        using var source = new World();

        source.Create(new RegistrationTestHandle { Slot = 1 });
        source.Create(new RegistrationTestHandle { Slot = 2 });
        source.Create(new Shield { Absorption = 1f }, new RegistrationTestHandle { Slot = 3 });

        var content = WorldSerializer.Capture(source);

        Assert.Equal(typeof(RegistrationTestHandle).FullName, Assert.Single(content.Dropped));
    }

    // ---------------------------------------------------------------- the format

    [Fact]
    public void The_content_survives_the_binary_serializer() {
        using var source = Populated();

        var content = WorldSerializer.Capture(source);
        var read = Serializer.Read<WorldContent>(Serializer.ToBytes(content));

        Assert.Equal(content.Count, read.Count);
        Assert.Equal(content.Parents, read.Parents);
        Assert.Equal(content.Blocks.Length, read.Blocks.Length);

        using var target = new World();
        WorldSerializer.Restore(read, target);

        Assert.Equal(Serializer.ToBytes(content), Serializer.ToBytes(WorldSerializer.Capture(target)));
    }

    /// <summary>Restoring is not merging, and the difference is stated rather than implied.</summary>
    [Fact]
    public void Restoring_clears_whatever_the_target_was_holding() {
        using var source = new World();
        source.Create(new Shield { Absorption = 1f });

        using var target = new World();

        for (var index = 0; index < 10; index++) {
            target.Create(new Shield { Absorption = 99f });
        }

        WorldSerializer.Restore(WorldSerializer.Capture(source), target);

        Assert.Equal(1, target.EntityCount);
    }

    [Fact]
    public void A_capture_reports_the_entity_at_each_index_when_asked() {
        using var source = new World();
        var root = Tree(source);

        List<Entity> order = [];
        WorldSerializer.Capture(source, order);

        Assert.Equal(4, order.Count);
        Assert.Equal(root, order[0]);
    }

    // ---------------------------------------------------------------- malformed input

    [Fact]
    public void A_parent_that_is_not_before_its_child_is_refused() {
        var content = new WorldContent {
            Count = 2,
            Parents = [1, -1],
            Blocks = [new() { Entities = [0, 1], Columns = [] }]
        };

        var failure = Assert.Throws<ArgumentException>(content.Validate);

        Assert.Contains("not before it", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_in_no_block_is_refused() {
        var content = new WorldContent {
            Count = 2,
            Parents = [-1, -1],
            Blocks = [new() { Entities = [0], Columns = [] }]
        };

        var failure = Assert.Throws<ArgumentException>(content.Validate);

        Assert.Contains("in no block", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capture_naming_a_component_this_build_lacks_fails_at_the_load() {
        var content = new WorldContent {
            Count = 1,
            Parents = [-1],
            Blocks = [new() { Entities = [0], Columns = [new() { Component = "NotAComponentAnyoneHas", Data = [] }] }]
        };

        using var target = new World();

        Assert.Throws<SceneComponentException>(() => WorldSerializer.Restore(content, target));
    }

    [Fact]
    public void A_built_in_column_this_build_lacks_fails_at_the_load() {
        var content = new WorldContent {
            Count = 1,
            Parents = [-1],
            Blocks = [new() { Entities = [0], Columns = [new() { Component = "$notathing", Data = [] }] }]
        };

        using var target = new World();
        var failure = Assert.Throws<SceneComponentException>(() => WorldSerializer.Restore(content, target));

        Assert.Contains("built-in world column", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>What each of an entity's children absorbs, in the order the child list holds them.</summary>
    /// <remarks>
    ///     Materialised by hand because <c>ChildSequence</c> is a struct with a <c>GetEnumerator</c>
    ///     and not an <c>IEnumerable</c> — which is what keeps walking a hierarchy allocation-free and
    ///     is why LINQ does not reach it.
    /// </remarks>
    static List<float> Absorptions(World world, Entity entity) {
        List<float> absorptions = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            absorptions.Add(world.Read<Shield>(child).Absorption);
        }

        return absorptions;
    }

    /// <summary>A root with three children, distinguishable by what they absorb.</summary>
    /// <remarks>
    ///     Parented back to front, because <c>Hierarchy.SetParent</c> prepends — so linking three,
    ///     two, one leaves the children in the order one, two, three, which is what the assertions
    ///     read.
    /// </remarks>
    static Entity Tree(World world) {
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        world.Add(root, new Shield { Absorption = 0f });

        for (var index = 3; index >= 1; index--) {
            var child = Hierarchy.CreateTransform(world, LocalTransform.At(new Vector3(index, 0f, 0f)));

            world.Add(child, new Shield { Absorption = index });
            Hierarchy.SetParent(world, child, root);
        }

        return root;
    }

    /// <summary>A world with a tree, a tag, and an entity carrying nothing at all.</summary>
    static World Populated() {
        var world = new World();

        Tree(world);
        world.Create<Warded>(default);
        world.Create();

        return world;
    }
}
