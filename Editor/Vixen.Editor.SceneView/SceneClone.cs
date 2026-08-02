// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>Copying an entity, and everything under it, into the same scene.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P4 Duplicate, and the thing doc 20's clipboard was blocked on.</b>
///         <c>EditorParity</c> files Cut, Copy, Paste and Duplicate as planned with the note that
///         cloning a subtree needs "a component-wise copy the engine does not have yet" — this is that
///         copy. It stays here rather than in the shell because what has to be cloned is a scene's
///         idea of an entity: its components, its name, its geometry and the parameters that generated
///         the geometry, none of which the command stack knows about.
///     </para>
///     <para>
///         ⚠ <b>The scene component registry is the filter, which is the same rule saving applies.</b>
///         What a file can carry is what a clone carries, so a duplicate is exactly what you would get
///         by saving the entity and reading it back — and nothing is copied that a build could not
///         compile. The hierarchy links are excluded for the reason they are excluded from a file:
///         they hold entity handles, and a copy of one names the original's parent.
///     </para>
///     <para>
///         ⚠ <b>Undoable through <see cref="SceneDocument.Create" /> per entity, inside one
///         transaction.</b> Duplicating a room of nine walls is one thing somebody did and has to be
///         one <c>Ctrl+Z</c>; it is nine entries underneath because the create command is what knows
///         how to give a handle back.
///     </para>
///     <para>
///         ⚠ <b>Behaviours are not copied yet, and that is stated rather than silent.</b> A
///         <c>Behavior</c> is an object with authored fields rather than a value in a column, so
///         cloning one means constructing an instance and copying the members the contract left in —
///         which is <c>SceneBehaviorRegistry</c>'s to answer and is doc 20's E1 rather than doc 24's
///         P4. A duplicated block-out wall has no behaviours to lose.
///     </para>
/// </remarks>
public static class SceneClone {
    /// <summary>Copies an entity and its subtree, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">What to copy.</param>
    /// <param name="parent">What to hang the copy from, or <see cref="Entity.Null" /> for the
    ///     original's own parent.</param>
    /// <param name="offset">How far to move the copy, in its parent's space.</param>
    /// <param name="name">What to call it, or null for the original's name.</param>
    /// <returns>The copy, or <see cref="Entity.Null" /> when there was nothing to copy.</returns>
    public static Entity Duplicate(
        SceneDocument document,
        Entity entity,
        Entity parent = default,
        Vector3 offset = default,
        string? name = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.World.IsAlive(entity)) {
            return Entity.Null;
        }

        var under = parent.IsNull ? Hierarchy.ParentOf(document.World, entity) : parent;

        using (document.Stack.BeginTransaction("Duplicate")) {
            return Copy(document, entity, under, offset, name);
        }
    }

    /// <summary>Copies several entities and their subtrees as one undoable act.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entities">What to copy.</param>
    /// <param name="offset">How far to move each copy, in its parent's space.</param>
    /// <param name="into">The copies, in the order the originals were given. Cleared first.</param>
    /// <returns>How many were copied.</returns>
    /// <remarks>
    ///     ⚠ <b>An entity carried inside another one that is also being copied is skipped.</b>
    ///     Duplicating a room and one of its walls together would otherwise produce the wall twice —
    ///     once inside the copied room and once beside it — which is the same filter
    ///     <c>ReparentCommand</c> applies to a drag of a parent and its child.
    /// </remarks>
    public static int Duplicate(
        SceneDocument document,
        IEnumerable<Entity> entities,
        Vector3 offset,
        List<Entity> into
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var roots = entities.Where(document.World.IsAlive).ToList();
        var wanted = roots.ToHashSet();

        using (document.Stack.BeginTransaction("Duplicate")) {
            foreach (var entity in roots) {
                if (Inside(document, entity, wanted)) {
                    continue;
                }

                var copy = Copy(document, entity, Hierarchy.ParentOf(document.World, entity), offset, null);

                if (!copy.IsNull) {
                    into.Add(copy);
                }
            }
        }

        return into.Count;
    }

    /// <summary>Copies every component a scene file could carry from one entity onto another.</summary>
    /// <param name="world">The world both are in.</param>
    /// <param name="from">The original.</param>
    /// <param name="to">The copy.</param>
    /// <returns>How many components were copied.</returns>
    /// <remarks>What makes a clone the same thing rather than a same-shaped thing, and it is public
    ///     because a paste, a prefab apply and a duplicate all want exactly this.</remarks>
    public static int Components(World world, Entity from, Entity to) {
        ArgumentNullException.ThrowIfNull(world);

        var copied = 0;

        foreach (var id in world.ArchetypeOf(from).Signature.Ids) {
            if (!SceneComponentRegistry.TryGet(ComponentRegistry.Get(id).Type, out var binder)) {
                continue;
            }

            // ⚠ The transform is skipped even though it is a scene component, because the caller has
            // already placed the copy — a duplicate offset by a metre that then had the original's
            // transform written over it would land back on top of what it was copied from.
            if (binder.ComponentType == typeof(LocalTransform)) {
                continue;
            }

            binder.AddTo(world, to, binder.ValueOn(world, from));
            copied++;
        }

        return copied;
    }

    static Entity Copy(SceneDocument document, Entity entity, Entity parent, Vector3 offset, string? name) {
        var world = document.World;

        var local = world.Has<LocalTransform>(entity) ? world.Read<LocalTransform>(entity) : LocalTransform.Identity;
        var placed = local with { Position = local.Position + offset };

        var copy = document.Create(name ?? document.NameOf(entity), placed, parent, made => Components(world, entity, made));

        // ⚠ The geometry goes through the same commands a verb would use, rather than being written
        // into the document directly. That is what makes an undo of a duplicate put the tables back
        // as well as the entity — and it is why a parametric copy stays parametric: the parameters
        // are what is recorded, and the mesh follows from them.
        if (document.ShapeOf(entity) is { } parameters) {
            document.Stack.Execute(ShapeCommand.Set(document, copy, parameters, "Duplicate"));
        } else if (document.MeshOf(entity) is { } mesh) {
            document.SetMesh(copy, new(mesh));
            document.Stack.Execute(EditMeshCommand.Rebuilt(document, copy, null, "Duplicate"));
        }

        foreach (var (group, material) in document.MaterialsOf(entity)) {
            document.SetMaterial(copy, group, material);
        }

        // ⚠ Children are copied in reverse, for the reason `SceneSerializer` gives at length:
        // `Hierarchy.Link` puts a new child at the head of the intrusive list, so creating them in
        // order leaves the copy holding its children backwards.
        List<Entity> children = [];

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        for (var index = children.Count - 1; index >= 0; index--) {
            Copy(document, children[index], copy, Vector3.Zero, null);
        }

        return copy;
    }

    static bool Inside(SceneDocument document, Entity entity, HashSet<Entity> others) {
        for (var at = Hierarchy.ParentOf(document.World, entity); !at.IsNull; at = Hierarchy.ParentOf(document.World, at)) {
            if (others.Contains(at)) {
                return true;
            }
        }

        return false;
    }
}
