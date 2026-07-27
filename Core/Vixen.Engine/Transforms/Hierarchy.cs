// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Engine.Transforms;

/// <summary>
///     Parenting: the operations that keep <see cref="Parent" />, <see cref="Child" />,
///     <see cref="Sibling" /> and <see cref="HierarchyDepth" /> consistent with each other.
/// </summary>
/// <remarks>
///     Four components describe one relationship, and every one of them can be written directly.
///     Nothing stops that, and everything that does it will eventually produce a list that loops
///     back on itself or a depth that disagrees with the parent chain. This is the only supported
///     way to change the shape of a hierarchy, and it is what the tests are written against.
/// </remarks>
public static class Hierarchy {
    /// <summary>Creates an entity with a transform, ready to be parented and rendered.</summary>
    /// <param name="world">The world.</param>
    /// <param name="local">Where it starts.</param>
    /// <returns>The entity.</returns>
    public static Entity CreateTransform(World world, LocalTransform local) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Create(
            local,
            new WorldTransform { Value = local.ToMatrix() },
            new HierarchyDepth()
        );
    }

    /// <summary>The entity's parent, or <see cref="Entity.Null" /> if it is a root.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Its parent.</returns>
    public static Entity ParentOf(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<Parent>(entity, out var parent) ? parent.Value : Entity.Null;
    }

    /// <summary>How far the entity is from a root.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Its depth, zero for a root.</returns>
    public static int DepthOf(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<HierarchyDepth>(entity, out var depth) ? depth.Value : 0;
    }

    /// <summary>The entity's children, in no guaranteed order.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Something to <c>foreach</c> over.</returns>
    /// <remarks>
    ///     Order is not creation order: a child is inserted at the head of the list, which is what
    ///     makes adding one O(1). Anything that needs a stable order sorts by something it owns.
    /// </remarks>
    public static ChildSequence ChildrenOf(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);
        return new(world, world.TryGet<Child>(entity, out var child) ? child.First : Entity.Null);
    }

    /// <summary>Whether one entity is the other's ancestor.</summary>
    /// <param name="world">The world.</param>
    /// <param name="ancestor">The candidate ancestor.</param>
    /// <param name="entity">The entity to walk up from.</param>
    /// <returns>Whether it was found.</returns>
    public static bool IsAncestorOf(World world, Entity ancestor, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        for (var walk = ParentOf(world, entity); !walk.IsNull; walk = ParentOf(world, walk)) {
            if (walk == ancestor) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Reparents an entity, keeping its world position where it was.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity to move.</param>
    /// <param name="parent">Its new parent, or <see cref="Entity.Null" /> to make it a root.</param>
    /// <exception cref="InvalidOperationException">
    ///     The new parent is the entity itself or one of its descendants, which would make a cycle.
    /// </exception>
    /// <remarks>
    ///     Unity's behaviour and the one users expect: the object does not visibly jump. The local
    ///     transform is rewritten so that the world transform comes out the same, which needs the
    ///     new parent's world matrix inverted — and if that matrix is not invertible (a parent scaled
    ///     to zero on some axis) the local transform is kept instead, because a silent NaN is worse
    ///     than a visible jump.
    /// </remarks>
    public static void SetParentKeepingWorldPosition(World world, Entity entity, Entity parent) {
        ArgumentNullException.ThrowIfNull(world);

        var worldMatrix = world.Read<WorldTransform>(entity).Value;
        SetParent(world, entity, parent);

        var relative = worldMatrix;

        if (!parent.IsNull) {
            if (!Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)) {
                return;
            }

            relative = worldMatrix * inverse;
        }

        if (Matrix4x4.Decompose(relative, out var scale, out var rotation, out var translation)) {
            ref var local = ref world.Get<LocalTransform>(entity);
            local.Position = translation;
            local.Rotation = rotation;
            local.Scale = scale;
        }
    }

    /// <summary>Reparents an entity, keeping its local transform and so moving it in the world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity to move.</param>
    /// <param name="parent">Its new parent, or <see cref="Entity.Null" /> to make it a root.</param>
    /// <exception cref="InvalidOperationException">
    ///     The new parent is the entity itself or one of its descendants, which would make a cycle.
    /// </exception>
    public static void SetParent(World world, Entity entity, Entity parent) {
        ArgumentNullException.ThrowIfNull(world);

        if (entity == parent) {
            throw new InvalidOperationException($"Entity {entity} cannot be its own parent.");
        }

        if (!parent.IsNull && IsAncestorOf(world, entity, parent)) {
            throw new InvalidOperationException(
                $"Making {parent} the parent of {entity} would close a cycle: {parent} is already "
                + $"below {entity}. A hierarchy that loops has no roots, and every walk over it runs "
                + "for ever."
            );
        }

        var current = ParentOf(world, entity);

        if (current == parent) {
            return;
        }

        if (!current.IsNull) {
            Unlink(world, entity, current);
        }

        if (parent.IsNull) {
            if (world.Has<Parent>(entity)) {
                world.Remove<Parent>(entity);
            }

            if (world.Has<Sibling>(entity)) {
                world.Remove<Sibling>(entity);
            }

            Redepth(world, entity, 0);
            return;
        }

        if (world.Has<Parent>(entity)) {
            world.Set(entity, new Parent { Value = parent });
        } else {
            world.Add(entity, new Parent { Value = parent });
        }

        Link(world, entity, parent);
        Redepth(world, entity, (short)(DepthOf(world, parent) + 1));
    }

    /// <summary>Destroys an entity and everything below it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="root">The subtree root.</param>
    /// <remarks>
    ///     Depth-first and bottom-up, so a child is never orphaned by its parent's destruction — an
    ///     orphan keeps a <see cref="Parent" /> pointing at a dead entity, and every walk over it
    ///     throws.
    /// </remarks>
    public static void DestroySubtree(World world, Entity root) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAlive(root)) {
            return;
        }

        foreach (var child in ChildrenOf(world, root)) {
            DestroySubtreeWithoutUnlinking(world, child);
        }

        var parent = ParentOf(world, root);

        if (!parent.IsNull) {
            Unlink(world, root, parent);
        }

        world.Destroy(root);
    }

    /// <summary>Records the destruction of an entity and everything below it, for playback later.</summary>
    /// <param name="world">The world, read to walk the subtree as it is now.</param>
    /// <param name="commands">Where to record.</param>
    /// <param name="root">The subtree root.</param>
    /// <remarks>
    ///     The walk happens now and the destruction happens at the sync point, so a subtree that is
    ///     reparented in between is destroyed as it was when the decision was made. That is the
    ///     behaviour a deferred command has to have: the alternative is that what gets destroyed
    ///     depends on what else ran in the same phase.
    /// </remarks>
    public static void DestroySubtree(World world, CommandBuffer commands, Entity root) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(commands);

        if (!world.IsAlive(root)) {
            return;
        }

        foreach (var child in ChildrenOf(world, root)) {
            DestroySubtree(world, commands, child);
        }

        commands.Destroy(root);
    }

    static void DestroySubtreeWithoutUnlinking(World world, Entity entity) {
        foreach (var child in ChildrenOf(world, entity)) {
            DestroySubtreeWithoutUnlinking(world, child);
        }

        world.Destroy(entity);
    }

    static void Link(World world, Entity entity, Entity parent) {
        var first = world.TryGet<Child>(parent, out var child) ? child.First : Entity.Null;
        var sibling = new Sibling { Next = first, Previous = Entity.Null };

        if (world.Has<Sibling>(entity)) {
            world.Set(entity, sibling);
        } else {
            world.Add(entity, sibling);
        }

        if (!first.IsNull) {
            world.Get<Sibling>(first).Previous = entity;
        }

        if (world.Has<Child>(parent)) {
            world.Get<Child>(parent).First = entity;
        } else {
            world.Add(parent, new Child { First = entity });
        }
    }

    static void Unlink(World world, Entity entity, Entity parent) {
        var sibling = world.Read<Sibling>(entity);

        if (sibling.Previous.IsNull) {
            world.Get<Child>(parent).First = sibling.Next;
        } else {
            world.Get<Sibling>(sibling.Previous).Next = sibling.Next;
        }

        if (!sibling.Next.IsNull) {
            world.Get<Sibling>(sibling.Next).Previous = sibling.Previous;
        }

        // The component goes when the list empties, so "has children" stays an archetype question.
        if (world.Read<Child>(parent).First.IsNull) {
            world.Remove<Child>(parent);
        }
    }

    static void Redepth(World world, Entity entity, short depth) {
        if (world.Has<HierarchyDepth>(entity)) {
            world.Get<HierarchyDepth>(entity).Value = depth;
        } else {
            world.Add(entity, new HierarchyDepth { Value = depth });
        }

        foreach (var child in ChildrenOf(world, entity)) {
            Redepth(world, child, (short)(depth + 1));
        }
    }

    /// <summary>An entity's children, walked through the intrusive list.</summary>
    public readonly struct ChildSequence(World world, Entity first) {
        /// <summary>Starts walking.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator() => new(world, first);

        /// <summary>Walks a child list.</summary>
        public struct Enumerator(World world, Entity first) {
            Entity next = first;

            /// <summary>The child.</summary>
            public Entity Current { get; private set; }

            /// <summary>Moves to the next child.</summary>
            /// <returns>Whether there was one.</returns>
            /// <remarks>
            ///     The next link is read before the body runs, so a loop that destroys or reparents
            ///     the child it is looking at does not lose its place — which is what
            ///     <see cref="DestroySubtree(World, Entity)" /> relies on.
            /// </remarks>
            public bool MoveNext() {
                if (next.IsNull) {
                    Current = Entity.Null;
                    return false;
                }

                Current = next;
                next = world.TryGet<Sibling>(Current, out var sibling) ? sibling.Next : Entity.Null;
                return true;
            }
        }
    }
}
