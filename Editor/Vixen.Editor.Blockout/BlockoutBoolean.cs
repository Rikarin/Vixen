// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's P6 verbs against a scene: the booleans, the cuts, and the apply that ends them.</summary>
/// <remarks>
///     <para>
///         <b>Non-destructive first, which is the whole point of the phase.</b> A subtract makes a new
///         entity whose geometry is derived from its operands; the operands become its hidden children
///         and stay editable. "The doorway should be twenty centimetres wider" is then dragging a box
///         that is still there rather than rebuilding a wall.
///     </para>
///     <para>
///         ⚠ <b>The cuts are destructive and the booleans are not, and that asymmetry is the honest
///         one.</b> A plane cut's operand is a plane, which is not an entity and has nowhere to
///         survive as one — so keeping it live would mean inventing a second kind of operand with its
///         own gizmo, its own row in the outliner and its own file format. A trim's operand <i>is</i> an
///         entity, and it is still destructive because the thing a trim is for is throwing the cutter
///         away afterwards.
///     </para>
/// </remarks>
public static class BlockoutBoolean {
    /// <summary>Combines the selected entities into one derived result, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="operation">Which boolean.</param>
    /// <param name="name">What to call the result, or null for the operation's own name.</param>
    /// <returns>The result entity, or <see cref="Entity.Null" /> when there was nothing to combine.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two operands or more, in selection order, and a difference is not commutative.</b>
    ///         The first thing selected is the one being cut — which is the rule every reference
    ///         toolset uses and the only one a designer can predict.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The operands are reparented in reverse.</b> <c>Hierarchy.SetParent</c> puts a new
    ///         child at the head of the intrusive list, so adding them in selection order leaves the
    ///         result holding them backwards — and for a subtract that is not a cosmetic difference,
    ///         it is the wrong answer.
    ///     </para>
    /// </remarks>
    public static Entity Combine(SceneDocument document, BooleanOperation operation, string? name = null) {
        ArgumentNullException.ThrowIfNull(document);

        var operands = document.Selection.Items.Where(document.World.IsAlive).ToList();

        if (operands.Count < 2) {
            return Entity.Null;
        }

        var world = document.World;

        var at = world.Has<LocalTransform>(operands[0])
            ? world.Read<LocalTransform>(operands[0]).Position
            : Vector3.Zero;

        var label = name ?? operation.ToString();

        using (document.Stack.BeginTransaction(label)) {
            var result = document.Create(label, LocalTransform.Identity with { Position = at });

            for (var index = operands.Count - 1; index >= 0; index--) {
                document.Reparent(operands[index], result);
                document.SetHidden(operands[index], true);
            }

            document.Stack.Execute(new BooleanCommand(document, result, new(operation, 0), label));
            document.Selection.Set(result);

            return result;
        }
    }

    /// <summary>Everything in either of the selected solids.</summary>
    /// <param name="document">The scene.</param>
    /// <returns>The result entity.</returns>
    public static Entity Union(SceneDocument document) => Combine(document, BooleanOperation.Union, "Union");

    /// <summary>The first selected solid, less the rest.</summary>
    /// <param name="document">The scene.</param>
    /// <returns>The result entity.</returns>
    public static Entity Subtract(SceneDocument document) =>
        Combine(document, BooleanOperation.Difference, "Subtract");

    /// <summary>Only what all the selected solids share.</summary>
    /// <param name="document">The scene.</param>
    /// <returns>The result entity.</returns>
    public static Entity Intersect(SceneDocument document) =>
        Combine(document, BooleanOperation.Intersection, "Intersect");

    /// <summary>Collapses a derived result into a plain mesh and deletes its operands, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The result entity, or <see cref="Entity.Null" /> for the selection.</param>
    /// <returns>Whether anything was collapsed.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's "then the destructive apply".</b> The geometry stays exactly as it is and
    ///         everything that produced it goes — which is what a designer does once the shape is
    ///         settled and they want the entity count back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The operands are deleted rather than unhidden.</b> Leaving them would put two
    ///         solids in the level where the designer sees one, both selectable, both compiled, and
    ///         both invisible until somebody turns visibility back on. An undo brings them back, which
    ///         is what makes deleting them the reversible choice rather than the destructive one.
    ///     </para>
    /// </remarks>
    public static bool Collapse(SceneDocument document, Entity entity = default) {
        ArgumentNullException.ThrowIfNull(document);

        List<Entity> targets = entity.IsNull
            ? [.. document.Selection.Items.Where(document.IsDerived)]
            : document.IsDerived(entity) ? [entity] : [];

        if (targets.Count == 0) {
            return false;
        }

        using (document.Stack.BeginTransaction("Apply Boolean")) {
            foreach (var target in targets) {
                var operands = SceneCsg.Operands(document, target);

                document.Stack.Execute(new BooleanCommand(document, target, null, "Apply Boolean"));
                document.Delete(operands);
            }
        }

        return true;
    }

    /// <summary>Cuts the selected solids with a plane, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="plane">Where to cut, in world space.</param>
    /// <param name="keepFront">Whether the half the normal points at is the one that survives.</param>
    /// <param name="cap">Whether the opening is closed with a face.</param>
    /// <returns>How many entities were cut.</returns>
    /// <remarks>
    ///     ⚠ <b>The plane arrives in world space and is taken into each mesh's own.</b> Cutting three
    ///     walls with the work plane is one gesture and they are at three transforms; a caller that
    ///     had to convert would be a caller that gets it wrong for the one wall somebody had rotated.
    /// </remarks>
    public static int PlaneCut(SceneDocument document, Plane plane, bool keepFront = false, bool cap = true) {
        ArgumentNullException.ThrowIfNull(document);

        var cut = 0;

        using (document.Stack.BeginTransaction("Plane Cut")) {
            foreach (var entity in document.Selection.Items.ToArray()) {
                if (document.MeshOf(entity) is not { } mesh) {
                    continue;
                }

                var made = MeshBoolean.PlaneCut(mesh, Local(document, entity, plane), keepFront, cap);

                if (made is null) {
                    continue;
                }

                Record(document, entity, made, "Plane Cut");
                cut++;
            }
        }

        return cut;
    }

    /// <summary>Cuts the first selected solid by the second's surface and deletes the cutter, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="keep">Whether the cutter survives.</param>
    /// <returns>Whether anything was trimmed.</returns>
    public static bool Trim(SceneDocument document, bool keep = false) {
        ArgumentNullException.ThrowIfNull(document);

        var chosen = document.Selection.Items.Where(entity => document.MeshOf(entity) is not null).ToList();

        if (chosen.Count < 2) {
            return false;
        }

        var target = chosen[0];
        var mesh = document.MeshOf(target)!;

        using (document.Stack.BeginTransaction("Trim")) {
            foreach (var cutter in chosen.Skip(1)) {
                var made = MeshBoolean.Trim(mesh, document.MeshOf(cutter)!, Relative(document, target, cutter));

                if (made is null) {
                    continue;
                }

                Record(document, target, made, "Trim");
                mesh = made;
            }

            if (!keep) {
                document.Delete(chosen.Skip(1));
            }

            document.Selection.Set(target);
        }

        return true;
    }

    /// <summary>Replaces an entity's geometry, undoably.</summary>
    static void Record(SceneDocument document, Entity entity, EditMesh made, string name) {
        var was = document.MeshOf(entity) is { } mesh ? new EditMesh(mesh) : null;

        // ⚠ A cut collapses a derivation, because the result of a boolean is a function of its
        // operands and a cut is not one of them — so leaving the node in place would mean the next
        // refresh quietly put the uncut geometry back.
        if (document.IsDerived(entity)) {
            document.Stack.Execute(new BooleanCommand(document, entity, null, name));
        }

        if (document.IsParametric(entity)) {
            document.Stack.Execute(ShapeCommand.Demote(document, entity, name));
        }

        document.SetMesh(entity, made);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, entity, was, name));
    }

    /// <summary>A world-space plane in one entity's own space.</summary>
    static Plane Local(SceneDocument document, Entity entity, Plane plane) {
        var world = document.World;

        if (!world.Has<WorldTransform>(entity)
            || !Matrix4x4.Invert(world.Read<WorldTransform>(entity).Value, out var inverse)) {
            return plane;
        }

        // A point on the plane and its normal, each taken through the inverse — the normal as a
        // direction, which is what keeps a cut square to a wall that has been rotated.
        var normal = Vector3.Normalize(plane.Normal);
        var origin = Matrix4x4.TransformPosition(normal * -plane.D, inverse);
        var direction = Matrix4x4.TransformDirection(normal, inverse);

        return direction.IsZero
            ? plane
            : new Plane(Vector3.Normalize(direction), -Vector3.Dot(Vector3.Normalize(direction), origin));
    }

    static Matrix4x4 Relative(SceneDocument document, Entity target, Entity other) {
        var world = document.World;

        if (!world.Has<WorldTransform>(target) || !world.Has<WorldTransform>(other)) {
            return Matrix4x4.Identity;
        }

        return Matrix4x4.Invert(world.Read<WorldTransform>(target).Value, out var inverse)
            ? world.Read<WorldTransform>(other).Value * inverse
            : Matrix4x4.Identity;
    }
}
