// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
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
    ///     <para>
    ///         ⚠ <b>The entry goes on the viewport's document, because that is what an entity belongs
    ///         to.</b> This is the ordinary case and it reads as one, which it did not when it was the
    ///         fall-through of two other branches.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>A drag of a prefab instance claims what it moved, in the same undo step.</b> The
    ///         override list is names and never a comparison — doc 47 § 4 — so a member no instance
    ///         claims <i>is</i> the template's, and the next reconcile writes the template's value back
    ///         over it. Before this, every gizmo drag of an instance was silently discarded by the next
    ///         open of the level; <c>InspectorField.Apply</c> was the only edit route in the editor that
    ///         said "this one is the instance's own". See <see cref="Claims" /> for what a drag claims.
    ///     </para>
    /// </remarks>
    public GizmoEdit? Record(in GizmoDrag drag) {
        var command = new TransformTargetsCommand(drag.Verb, drag.Targets, drag.Captured, drag.Document);

        if (command.IsEmpty) {
            return null;
        }

        // One entry either way: the claim is part of the move rather than a second step beside it, so
        // the history still reads "Move" and a Ctrl+Z cannot leave the level claiming a member whose
        // value it has just given back — `PrefabSource.Claim`'s own reason, restated for the viewport.
        var claimed = Claims(drag);

        return new(
            claimed is null ? command : new CompositeCommand(command.Name, command, claimed),
            drag.Document?.Stack
        );
    }

    /// <summary>The claim a finished drag records, or nothing when it claims nothing.</summary>
    /// <param name="drag">The drag, already applied.</param>
    /// <returns>The command that marks the members, or <see langword="null" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the members whose value actually changed.</b> Claiming all three on every move
    ///         would mark <c>Rotation</c> and <c>Scale</c> on the first drag of every instance in a
    ///         level and block the template's next change to them for ever, silently — a larger loss
    ///         than the one this fixes. The predicate is exact equality, which is the same one
    ///         <see cref="TransformTargetsCommand.IsEmpty" /> already uses to decide a drag happened at
    ///         all: if it is good enough to say "nothing moved" it is good enough to say "this member
    ///         did not".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the targets, so a child carried along by its parent's drag claims nothing.</b>
    ///         Every descendant's world transform moves when a root does, and a rule over "ended up
    ///         somewhere else" would mark the whole subtree. <c>PrefabInstances.Mark</c> refuses an
    ///         entity with no link, so an ordinary object costs a dictionary probe and nothing else.
    ///     </para>
    ///     <para>
    ///         <b>Written here rather than through <c>IPrefabSource</c>.</b> That seam is the
    ///         inspector's — it maps an inspected <i>object</i> and an <c>InspectorMember</c> onto an
    ///         entity and a path, and neither exists during a drag — and it lives in an assembly this
    ///         one cannot see. What is shared is the store underneath, <see cref="PrefabInstances" />,
    ///         which is where the rule actually is.
    ///     </para>
    /// </remarks>
    static IEditorCommand? Claims(in GizmoDrag drag) {
        if (drag.Document is not SceneDocument scene) {
            return null;
        }

        var instances = scene.Prefabs;
        List<(Entity Entity, string Member)> claims = [];
        var count = Math.Min(drag.Targets.Count, drag.Captured.Count);

        for (var index = 0; index < count; index++) {
            if (drag.Targets[index] is not EntityGizmoTarget target || !instances.TryGet(target.Entity, out _)) {
                continue;
            }

            var before = drag.Captured[index];

            Claim(claims, instances, target.Entity, nameof(SceneEntityData.Position), before.Position != target.Position);
            Claim(claims, instances, target.Entity, nameof(SceneEntityData.Rotation), before.Rotation != target.Rotation);
            Claim(claims, instances, target.Entity, nameof(SceneEntityData.Scale), before.Scale != target.Scale);
        }

        if (claims.Count == 0) {
            return null;
        }

        return new DelegateCommand(
            "Override Transform",
            _ => {
                foreach (var claim in claims) {
                    instances.Mark(claim.Entity, claim.Member);
                }
            },
            _ => {
                foreach (var claim in claims) {
                    instances.Clear(claim.Entity, claim.Member);
                }
            }
        );
    }

    /// <summary>Adds a member to the list when the drag moved it and the instance had not claimed it.</summary>
    /// <remarks>
    ///     ⚠ <b>Already-claimed members are filtered out here rather than in the command</b>, so that
    ///     undo clears exactly what <c>Do</c> marked. A drag that re-moved a member claimed by an
    ///     earlier one contributes nothing, and its Ctrl+Z leaves the earlier claim standing — which is
    ///     what the author said and never took back.
    /// </remarks>
    static void Claim(
        List<(Entity Entity, string Member)> claims,
        PrefabInstances instances,
        Entity entity,
        string member,
        bool moved
    ) {
        if (moved && !instances.IsOverridden(entity, member)) {
            claims.Add((entity, member));
        }
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
