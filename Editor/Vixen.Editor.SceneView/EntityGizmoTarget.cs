// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>An entity, as something the gizmo can move.</summary>
/// <remarks>
///     <para>
///         The adapter between the gizmo's arithmetic — which knows nothing about worlds — and the
///         ECS. It is a class rather than a <c>ref struct</c> over <c>Transform</c> because the gizmo
///         holds its targets across the frames of a drag, and the engine's <c>Transform</c> façade is
///         deliberately a view that cannot outlive the frame it was made in.
///     </para>
///     <para>
///         ⚠ <b>Reads and writes go straight to the chunk, every time.</b> Nothing is cached: two
///         adapters over one entity therefore cannot disagree, and a script that moved the entity
///         mid-drag is seen rather than overwritten. The cost is a component lookup per property
///         access, which against a drag's handful of targets per frame is not a cost.
///     </para>
///     <para>
///         ⚠ <b>World rotation is written back through the parent's inverse</b>, and the parent's
///         world transform is whatever the last <c>TransformSystem</c> pass produced. Dragging a
///         child in the same frame as its parent therefore lands relative to where the parent was at
///         the start of the frame and is corrected next pass — which is exactly what
///         <c>Transform</c>'s own remarks say about setting a world-space property, and it is Unity's
///         behaviour too.
///     </para>
/// </remarks>
public sealed class EntityGizmoTarget : IGizmoTarget {
    readonly World world;

    /// <summary>The entity being moved.</summary>
    public Entity Entity { get; }

    /// <summary>Views an entity as a gizmo target.</summary>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The entity.</param>
    public EntityGizmoTarget(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        this.world = world;
        Entity = entity;
    }

    /// <inheritdoc />
    public Vector3 Position {
        get => new Transform(world, Entity).Position;
        set => new Transform(world, Entity).Position = value;
    }

    /// <inheritdoc />
    public Quaternion Rotation {
        get => new Transform(world, Entity).Rotation;
        set => new Transform(world, Entity).Rotation = value;
    }

    /// <inheritdoc />
    public Vector3 Scale {
        get => new Transform(world, Entity).LocalScale;
        set => new Transform(world, Entity).LocalScale = value;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The identity for a root, which is what makes <see cref="GizmoSpace.Parent" /> mean world
    ///     space for an object that has no parent — the right answer, and the same one the stored
    ///     <c>LocalTransform</c> is already in.
    /// </remarks>
    public Matrix4x4 ParentToWorld {
        get {
            var parent = Hierarchy.ParentOf(world, Entity);

            return parent.IsNull || !world.Has<WorldTransform>(parent)
                ? Matrix4x4.Identity
                : world.Read<WorldTransform>(parent).Value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The entry goes on the viewport's document, because that is what an entity belongs
    ///     to.</b> This is the ordinary case and it reads as one, which it did not when it was the
    ///     fall-through of two other branches.
    /// </remarks>
    public GizmoEdit? Record(in GizmoDrag drag) {
        var command = new TransformTargetsCommand(drag.Verb, drag.Targets, drag.Captured, drag.Document);

        return command.IsEmpty ? null : new(command, drag.Document?.Stack);
    }

    /// <summary>Views a selection of entities as gizmo targets.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entities">The entities, in selection order.</param>
    /// <returns>The targets, skipping anything that is not alive or has no transform.</returns>
    /// <remarks>
    ///     ⚠ <b>A dead entity is skipped rather than throwing.</b> A selection outlives the thing it
    ///     names — undo deletes an object that is still selected, a script destroys one — and an
    ///     editor that threw while drawing its gizmo would be unusable for the rest of the session.
    /// </remarks>
    public static IReadOnlyList<EntityGizmoTarget> For(World world, IEnumerable<Entity> entities) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entities);

        List<EntityGizmoTarget> targets = [];

        foreach (var entity in entities) {
            if (world.IsAlive(entity) && world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity)) {
                targets.Add(new(world, entity));
            }
        }

        return targets;
    }
}
