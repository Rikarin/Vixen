// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class HierarchyTests {
    [Fact]
    public void ANewEntityIsARootWithNoChildren() {
        using var world = new World();
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Assert.True(Hierarchy.ParentOf(world, entity).IsNull);
        Assert.Equal(0, Hierarchy.DepthOf(world, entity));
        Assert.Empty(Children(world, entity));
        Assert.False(world.Has<Parent>(entity));
    }

    [Fact]
    public void ParentingLinksBothDirections() {
        using var world = new World();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, child, parent);

        Assert.Equal(parent, Hierarchy.ParentOf(world, child));
        Assert.Equal([child], Children(world, parent));
        Assert.Equal(1, Hierarchy.DepthOf(world, child));
    }

    [Fact]
    public void SeveralChildrenAllAppearInTheList() {
        using var world = new World();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var children = new List<Entity>();

        for (var index = 0; index < 5; index++) {
            var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);
            Hierarchy.SetParent(world, child, parent);
            children.Add(child);
        }

        Assert.Equal([.. children.Order()], [.. Children(world, parent).Order()]);
    }

    [Fact]
    public void UnparentingLeavesTheParentWithoutAChildComponent() {
        using var world = new World();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, child, parent);
        Hierarchy.SetParent(world, child, Entity.Null);

        Assert.True(Hierarchy.ParentOf(world, child).IsNull);
        Assert.False(world.Has<Child>(parent));
        Assert.False(world.Has<Sibling>(child));
        Assert.Equal(0, Hierarchy.DepthOf(world, child));
    }

    [Fact]
    public void RemovingAMiddleChildKeepsTheListIntact() {
        using var world = new World();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var children = new List<Entity>();

        for (var index = 0; index < 5; index++) {
            var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);
            Hierarchy.SetParent(world, child, parent);
            children.Add(child);
        }

        Hierarchy.SetParent(world, children[2], Entity.Null);

        Assert.Equal(4, Children(world, parent).Count);
        Assert.DoesNotContain(children[2], Children(world, parent));
    }

    [Fact]
    public void ReparentingMovesTheWholeSubtreesDepth() {
        using var world = new World();
        var a = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var b = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var c = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var d = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, c, b);
        Hierarchy.SetParent(world, d, c);
        Assert.Equal(0, Hierarchy.DepthOf(world, b));
        Assert.Equal(2, Hierarchy.DepthOf(world, d));

        Hierarchy.SetParent(world, b, a);

        Assert.Equal(1, Hierarchy.DepthOf(world, b));
        Assert.Equal(2, Hierarchy.DepthOf(world, c));
        Assert.Equal(3, Hierarchy.DepthOf(world, d));
    }

    /// <summary>A hierarchy that loops has no roots, and every walk over it runs for ever.</summary>
    [Fact]
    public void ACycleIsRefusedRatherThanBuilt() {
        using var world = new World();
        var a = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var b = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, b, a);

        Assert.Throws<InvalidOperationException>(() => Hierarchy.SetParent(world, a, b));
        Assert.Throws<InvalidOperationException>(() => Hierarchy.SetParent(world, a, a));
    }

    [Fact]
    public void DestroyingASubtreeTakesEverythingBelowIt() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var kept = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var branch = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var leaf = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, branch, root);
        Hierarchy.SetParent(world, leaf, branch);
        Hierarchy.SetParent(world, kept, root);

        Hierarchy.DestroySubtree(world, branch);

        Assert.False(world.IsAlive(branch));
        Assert.False(world.IsAlive(leaf));
        Assert.True(world.IsAlive(kept));
        Assert.Equal([kept], Children(world, root));
    }

    [Fact]
    public void DestroyingASubtreeThroughACommandBufferRemovesTheSameEntities() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var branch = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var leaf = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, branch, root);
        Hierarchy.SetParent(world, leaf, branch);

        var commands = new CommandBuffer(world);
        Hierarchy.DestroySubtree(world, commands, branch);

        Assert.True(world.IsAlive(leaf));
        commands.Playback();

        Assert.False(world.IsAlive(branch));
        Assert.False(world.IsAlive(leaf));
        Assert.True(world.IsAlive(root));
    }

    /// <summary>
    ///     Randomised reparent and destroy against a reference tree, which is what
    ///     [04](../../../docs/plan/04-ecs-and-scripting.md) § Tests asks for.
    /// </summary>
    [Fact]
    public void RandomisedReparentingAgreesWithAReferenceTree() {
        for (var seed = 1; seed <= 60; seed++) {
            var state = (uint)seed;

            uint Next() {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return state;
            }

            using var world = new World();
            var entities = new List<Entity>();
            var parentOf = new Dictionary<Entity, Entity>();

            for (var index = 0; index < 24; index++) {
                var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);
                entities.Add(entity);
                parentOf[entity] = Entity.Null;
            }

            for (var step = 0; step < 200; step++) {
                var child = entities[(int)(Next() % (uint)entities.Count)];

                if (!world.IsAlive(child)) {
                    continue;
                }

                var choice = Next() % 10;

                if (choice < 2) {
                    // Destroy the subtree, and mirror it in the model.
                    var doomed = Descendants(parentOf, child);

                    foreach (var gone in doomed) {
                        parentOf.Remove(gone);
                    }

                    Hierarchy.DestroySubtree(world, child);
                } else if (choice < 4) {
                    Hierarchy.SetParent(world, child, Entity.Null);
                    parentOf[child] = Entity.Null;
                } else {
                    var parent = entities[(int)(Next() % (uint)entities.Count)];

                    if (!world.IsAlive(parent) || parent == child || IsBelow(parentOf, child, parent)) {
                        continue;
                    }

                    Hierarchy.SetParent(world, child, parent);
                    parentOf[child] = parent;
                }

                foreach (var (entity, parent) in parentOf) {
                    Assert.True(world.IsAlive(entity));
                    Assert.Equal(parent, Hierarchy.ParentOf(world, entity));
                    Assert.Equal(DepthIn(parentOf, entity), Hierarchy.DepthOf(world, entity));

                    var expected = parentOf.Where(pair => pair.Value == entity).Select(pair => pair.Key).Order();
                    Assert.Equal([.. expected], [.. Children(world, entity).Order()]);
                }

                Assert.Equal(parentOf.Count, world.EntityCount);
            }
        }
    }

    static List<Entity> Descendants(Dictionary<Entity, Entity> parentOf, Entity root) {
        var found = new List<Entity> { root };

        for (var index = 0; index < found.Count; index++) {
            found.AddRange(parentOf.Where(pair => pair.Value == found[index]).Select(pair => pair.Key));
        }

        return found;
    }

    static bool IsBelow(Dictionary<Entity, Entity> parentOf, Entity ancestor, Entity entity) {
        for (var walk = entity; !walk.IsNull; walk = parentOf.GetValueOrDefault(walk)) {
            if (walk == ancestor) {
                return true;
            }
        }

        return false;
    }

    static int DepthIn(Dictionary<Entity, Entity> parentOf, Entity entity) {
        var depth = 0;

        for (var walk = parentOf[entity]; !walk.IsNull; walk = parentOf[walk]) {
            depth++;
        }

        return depth;
    }

    static List<Entity> Children(World world, Entity entity) {
        var children = new List<Entity>();

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }
}
