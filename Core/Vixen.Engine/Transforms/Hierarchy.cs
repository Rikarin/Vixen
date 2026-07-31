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

    /// <summary>
    ///     The entity's local-to-world matrix as it stands <i>now</i>, composed by walking the parent
    ///     chain rather than read from <see cref="WorldTransform" />.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The matrix, or the identity for an entity with no transform.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>For the code that runs before <c>TransformSystem</c> does.</b>
    ///         <see cref="WorldTransform" /> is resolved in <c>SystemPhase.PreRender</c>, so anything
    ///         reading it during <c>Update</c> or <c>LateUpdate</c> is reading what the entity's
    ///         position was at the end of the previous frame. That is fine for most things and is
    ///         exactly wrong for a camera: a camera that follows a target through last frame's
    ///         position renders every frame one frame behind the thing it is looking at, which shows
    ///         up as a subject that slides about within the frame whenever anything accelerates.
    ///     </para>
    ///     <para>
    ///         It costs one matrix multiply per level of depth, per call, and it is worth it exactly
    ///         where the entity count is small and the staleness is visible. Anything sweeping over
    ///         thousands of entities should be in <c>PreRender</c> reading the resolved column
    ///         instead.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 ResolveWorldMatrix(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.TryGet<LocalTransform>(entity, out var local)) {
            return world.TryGet<WorldTransform>(entity, out var resolved) ? resolved.Value : Matrix4x4.Identity;
        }

        var matrix = local.ToMatrix();

        // Row-vector convention: the child's own transform applies first, then each ancestor's in
        // turn. The same composition TransformSystem performs, walked upward instead of downward.
        for (var parent = ParentOf(world, entity); !parent.IsNull; parent = ParentOf(world, parent)) {
            if (!world.TryGet<LocalTransform>(parent, out var parentLocal)) {
                break;
            }

            matrix *= parentLocal.ToMatrix();
        }

        return matrix;
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

    /// <summary>Hangs an entity from a parent at a particular place among its children.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity to move.</param>
    /// <param name="parent">Its new parent, which may not be null — a root has no siblings to sit among.</param>
    /// <param name="after">
    ///     The child to sit behind, or <see cref="Entity.Null" /> to go first. Must already be a child
    ///     of <paramref name="parent" />.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     The new parent is the entity itself or one of its descendants, or
    ///     <paramref name="after" /> is not a child of <paramref name="parent" />.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>What undo needs and <see cref="SetParent" /> cannot give it.</b> Linking prepends —
    ///         O(1), and the right default for building a hierarchy — so undoing a delete or a
    ///         reparent with it puts the entity back at the head of its old parent's children rather
    ///         than where it was. A user who moves the third of five children and presses Ctrl+Z gets
    ///         it back in the wrong place, which is an undo that did not undo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The neighbour is the position.</b> An index would have to be counted from the
    ///         head, be invalidated by every insertion before it, and mean nothing once a sibling was
    ///         itself deleted. The entity that used to be in front is stable under all three — and it
    ///         is what the list already stores, so restoring is a link rather than a walk.
    ///     </para>
    /// </remarks>
    public static void SetParentAfter(World world, Entity entity, Entity parent, Entity after) {
        ArgumentNullException.ThrowIfNull(world);

        if (parent.IsNull) {
            throw new InvalidOperationException(
                $"Entity {entity} cannot be placed after {after} with no parent: roots are not a "
                + "sibling list, so there is no order to restore. Use SetParent for a root."
            );
        }

        if (!after.IsNull && ParentOf(world, after) != parent) {
            throw new InvalidOperationException(
                $"Entity {after} is not a child of {parent}, so there is no place behind it. A "
                + "position recorded before something else moved is a position that no longer exists; "
                + "the caller has to notice that rather than have it silently become 'first'."
            );
        }

        SetParent(world, entity, parent);

        if (after.IsNull) {
            // Already first: SetParent prepends, which is where this wants it.
            return;
        }

        Unlink(world, entity, parent);
        LinkAfter(world, entity, parent, after);
    }

    /// <summary>Which child comes before this one, or null if it is the first or has no parent.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The previous sibling.</returns>
    /// <remarks>What to record before moving something, so <see cref="SetParentAfter" /> can undo it.</remarks>
    public static Entity PreviousSiblingOf(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.TryGet<Sibling>(entity, out var sibling) ? sibling.Previous : Entity.Null;
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

    /// <summary>Splices an entity into a parent's list behind one of its children.</summary>
    /// <remarks>
    ///     ⚠ <b>The entity's own component is written first, and the order is not stylistic.</b>
    ///     Adding a component is a structural change: it moves the entity to another archetype, which
    ///     moves rows in the chunks it leaves and enters. A <c>ref</c> taken before that — to
    ///     <paramref name="after" />'s sibling record, say — points at whatever now occupies the row.
    ///     <see cref="Link" /> is written in the same order for the same reason.
    /// </remarks>
    static void LinkAfter(World world, Entity entity, Entity parent, Entity after) {
        var next = world.Read<Sibling>(after).Next;
        var sibling = new Sibling { Next = next, Previous = after };

        if (world.Has<Sibling>(entity)) {
            world.Set(entity, sibling);
        } else {
            world.Add(entity, sibling);
        }

        world.Get<Sibling>(after).Next = entity;

        if (!next.IsNull) {
            world.Get<Sibling>(next).Previous = entity;
        }

        // The head cannot have changed — this went behind something, so something is still in front.
        // Said rather than assumed, because `Link`'s counterpart does have to touch it.
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
