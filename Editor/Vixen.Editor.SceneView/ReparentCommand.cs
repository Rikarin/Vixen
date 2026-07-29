// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>Moving entities to a new parent, as something the undo stack can take back.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B1 lists "drag-to-reparent (undoably — the primitive
///         <c>Hierarchy.SetParentAfter</c> exists, the command does not)", and this is the
///         command.</b> <c>SceneDocument.Reparent</c> said as much in its own remarks: reparenting
///         <i>is</i> reversible, and undoing it has to put the entity back among its old siblings
///         where it was rather than at the head of them.
///     </para>
///     <para>
///         ⚠ <b>The position is recorded as a neighbour, not an index.</b> An index would have to be
///         counted from the head, would be invalidated by every insertion before it, and would mean
///         nothing once a sibling was itself deleted. The entity that used to be in front is stable
///         under all three, and it is what the intrusive sibling list already stores — so restoring
///         is a link rather than a walk. <c>Hierarchy.SetParentAfter</c>'s own remarks make the same
///         argument from the other side.
///     </para>
///     <para>
///         ⚠ <b>A root has no neighbour to record, and that is not an oversight.</b> Roots are not a
///         sibling list — the scene finds them by walking every entity with no parent — so an entity
///         undone back to the root set comes back in creation order rather than where it was
///         dragged from. Making that exact needs the scene to hold a root order, which is a change to
///         the format rather than to this.
///     </para>
///     <para>
///         ⚠ <b>The world position is kept, which is what a drag in an outliner means.</b> Dragging
///         a crate onto a shelf should not teleport it inside the shelf's local space; every editor
///         behaves this way and the one that does not is reported as a bug on the first afternoon.
///     </para>
/// </remarks>
public sealed class ReparentCommand : IEditorCommand {
    /// <summary>One entity, where it is going, and where it came from.</summary>
    readonly record struct Move(Entity Entity, Entity Parent, Entity WasParent, Entity WasAfter);

    readonly SceneDocument document;
    readonly Move[] moves;

    /// <inheritdoc />
    public string Name => moves.Length == 1 ? "Reparent Entity" : "Reparent Entities";

    /// <summary>Describes a move that has not happened yet.</summary>
    /// <param name="document">The scene the entities belong to.</param>
    /// <param name="entities">What to move.</param>
    /// <param name="parent">Their new parent, or <see cref="Entity.Null" /> to make them roots.</param>
    /// <remarks>
    ///     ⚠ <b>What cannot move is dropped here rather than refused at execution.</b> An entity
    ///     already under that parent, one that is dead, and one that is an ancestor of the parent —
    ///     which would make a cycle the transform pass walks for ever — are all filtered now, so
    ///     <see cref="IsEmpty" /> is what a caller asks before putting this on the stack. A command
    ///     that executed to nothing would be an undo step that appears to do nothing.
    /// </remarks>
    public ReparentCommand(SceneDocument document, IEnumerable<Entity> entities, Entity parent) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entities);

        this.document = document;

        List<Move> planned = [];
        var world = document.World;

        foreach (var entity in entities) {
            if (!world.IsAlive(entity) || entity == parent) {
                continue;
            }

            if (Hierarchy.ParentOf(world, entity) == parent) {
                continue;
            }

            if (!parent.IsNull && Hierarchy.IsAncestorOf(world, entity, parent)) {
                continue;
            }

            // ⚠ Skipped when an ancestor is also moving, and this is the one that is easy to miss.
            // Dragging a parent and its child together means the child is carried by the parent; a
            // second move for it would take it out of the subtree it just travelled inside, and
            // undoing would then have to put it back in an order that no longer exists.
            if (Carried(world, entity, entities)) {
                continue;
            }

            planned.Add(
                new Move(
                    entity,
                    parent,
                    Hierarchy.ParentOf(world, entity),
                    Hierarchy.PreviousSiblingOf(world, entity)
                )
            );
        }

        moves = [.. planned];
    }

    /// <summary>Whether there is nothing left to do.</summary>
    public bool IsEmpty => moves.Length == 0;

    /// <summary>How many entities this moves.</summary>
    public int Count => moves.Length;

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var move in moves) {
            Place(move.Entity, move.Parent, Entity.Null);
        }

        Done(context);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Backwards, and it matters when several entities came from one parent.</b> Each was
    ///     recorded behind the sibling that was in front of it <i>at the time</i>, so putting the
    ///     last one back first restores the chain from its tail — the neighbour a move refers to is
    ///     always already in place by the time it is used.
    /// </remarks>
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = moves.Length - 1; index >= 0; index--) {
            var move = moves[index];
            Place(move.Entity, move.WasParent, move.WasAfter);
        }

        Done(context);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Never merged.</b> Two drags are two things somebody did, and a user who moves a crate
    ///     onto a shelf and then onto the floor expects two presses of Ctrl+Z to get back — the
    ///     transform commands merge because a drag is a hundred frames of one gesture, which this is
    ///     not.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <summary>Puts one entity under a parent, behind a neighbour where there is one.</summary>
    void Place(Entity entity, Entity parent, Entity after) {
        var world = document.World;

        if (!world.IsAlive(entity)) {
            return;
        }

        if (parent.IsNull) {
            // A root, which has no sibling list and therefore no position to restore.
            Hierarchy.SetParentKeepingWorldPosition(world, entity, Entity.Null);
            return;
        }

        if (!world.IsAlive(parent)) {
            return;
        }

        // ⚠ The two halves of `SetParentKeepingWorldPosition` done by hand, because that method
        // links with `SetParent` — which prepends, and prepending is exactly what this command
        // exists to avoid on the way back. Keeping the world position and keeping the position among
        // the siblings are both promises here, and no single primitive makes both.
        var matrix = world.Read<WorldTransform>(entity).Value;

        // A neighbour that has since moved elsewhere is no longer a position — `SetParentAfter`
        // throws rather than silently meaning "first", and an undo is not the moment to throw.
        var behind = !after.IsNull && Hierarchy.ParentOf(world, after) == parent ? after : Entity.Null;

        Hierarchy.SetParentAfter(world, entity, parent, behind);
        Keep(world, entity, parent, matrix);
    }

    /// <summary>Rewrites the local transform so the world one comes out unchanged.</summary>
    /// <remarks>
    ///     ⚠ <b>A parent scaled to zero on some axis has no invertible matrix, and the local
    ///     transform is kept instead.</b> `Hierarchy.SetParentKeepingWorldPosition` makes the same
    ///     choice for the same reason: a silent NaN spreading through every descendant is worse than
    ///     a visible jump.
    /// </remarks>
    static void Keep(World world, Entity entity, Entity parent, Matrix4x4 matrix) {
        if (!Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)) {
            return;
        }

        if (Matrix4x4.Decompose(matrix * inverse, out var scale, out var rotation, out var translation)) {
            ref var local = ref world.Get<LocalTransform>(entity);

            local.Position = translation;
            local.Rotation = rotation;
            local.Scale = scale;
        }
    }

    void Done(EditorContext context) {
        document.RaiseStructureChanged();
        context.Touch(document);
    }

    /// <summary>Whether an ancestor of this entity is in the same move.</summary>
    static bool Carried(World world, Entity entity, IEnumerable<Entity> moving) {
        for (var parent = Hierarchy.ParentOf(world, entity); !parent.IsNull;
             parent = Hierarchy.ParentOf(world, parent)) {
            foreach (var candidate in moving) {
                if (candidate == parent) {
                    return true;
                }
            }
        }

        return false;
    }
}
