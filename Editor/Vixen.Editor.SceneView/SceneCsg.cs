// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>An entity whose geometry is a boolean of its children's.</summary>
/// <param name="Operation">Which boolean.</param>
/// <param name="Inputs">
///     What the operands looked like when the result was last built, so that a change to one of them
///     is noticed without the document having to describe what changed.
/// </param>
/// <remarks>
///     ⚠ <b>The operands are the entity's <i>children</i> rather than a list of references.</b> A
///     reference would be a second way for one entity to name another, with its own lifetime rules —
///     what happens when an operand is deleted, what a duplicate of the result does about them, what
///     the outliner draws. Children answer all of that for free: deleting the result deletes its
///     operands, duplicating it duplicates them, and the outliner shows the tree it already shows.
/// </remarks>
public readonly record struct CsgNode(BooleanOperation Operation, int Inputs);

/// <summary>Doc 24's P6 non-destructive boolean: the operands survive and the result is derived.</summary>
/// <remarks>
///     <para>
///         <b>"The operands stay, the result is derived, and changing an operand re-evaluates it."</b>
///         That is the whole of the feature and it is why the boolean is worth having at all: a
///         subtract that collapsed the two solids into one result would make "the doorway should be
///         twenty centimetres wider" a rebuild, where non-destructively it is dragging a box that is
///         still there.
///     </para>
///     <para>
///         ⚠ <b>The operands are hidden rather than deleted, and hidden is editor state.</b>
///         <c>SceneDocument.Hidden</c> is not written to the scene file and not a component — hiding
///         something to work on what is behind it must not change what ships — so an operand is
///         invisible in the viewport, still selectable in the outliner, and still exactly what a build
///         would compile if anybody asked for it. Which nobody does, because the <i>result</i> is what
///         carries the geometry.
///     </para>
///     <para>
///         ⚠ <b>Re-evaluation is pulled rather than pushed.</b> <see cref="Refresh" /> compares one
///         integer per node — a hash of its operands' mesh versions and transforms — so a frame that
///         changed nothing costs a comparison per boolean in the scene. An event per operand would
///         mean every drag of every box firing through a graph, and the ordering of two operands that
///         both changed in one frame would decide how many times the result was rebuilt.
///     </para>
///     <para>
///         ⚠ <b>Nested booleans work because a node's operand may be a node.</b> The evaluation is
///         depth-first over the children, so a subtract of a union is two nodes and one traversal —
///         which is how a designer actually builds a doorway with a window over it.
///     </para>
/// </remarks>
public static class SceneCsg {
    /// <summary>Rebuilds every boolean in the document whose operands have changed.</summary>
    /// <param name="document">The scene.</param>
    /// <returns>How many were rebuilt.</returns>
    /// <remarks>What a frame calls, beside <c>MeshEdit.Reconcile</c> and for the same reason: it is
    ///     cheap when nothing has happened, and the alternative is every caller knowing which
    ///     booleans its edit was upstream of.</remarks>
    public static int Refresh(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Booleans.Count == 0) {
            return 0;
        }

        var rebuilt = 0;

        // ⚠ Deepest first, so that a boolean whose operand is another boolean sees the operand's new
        // geometry in the same pass rather than one frame later. Sorting by depth is cheaper than a
        // dependency walk and gives the same order, because an operand is always a child.
        foreach (var entity in document.Booleans.Keys.OrderByDescending(entity => Depth(document, entity)).ToArray()) {
            if (Evaluate(document, entity, force: false)) {
                rebuilt++;
            }
        }

        return rebuilt;
    }

    /// <summary>Rebuilds one boolean from its operands.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The result entity.</param>
    /// <param name="force">Whether to rebuild even when nothing has changed.</param>
    /// <returns>Whether the geometry changed.</returns>
    /// <remarks>
    ///     ⚠ <b>An operation that comes out empty leaves the result with no mesh rather than deleting
    ///     the entity.</b> Dragging a cutter until it swallows the thing it is cutting is a normal
    ///     moment in the middle of a gesture, and an entity that deleted itself at that moment would
    ///     take its operands with it and leave nothing to drag back.
    /// </remarks>
    public static bool Evaluate(SceneDocument document, Entity entity, bool force = true) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.BooleanOf(entity) is not { } node) {
            return false;
        }

        var inputs = Fingerprint(document, entity);

        if (!force && inputs == node.Inputs) {
            return false;
        }

        var made = Combine(document, entity, node.Operation);

        document.SetMesh(entity, made);
        document.SetBoolean(entity, node with { Inputs = inputs });

        return true;
    }

    /// <summary>Works out the geometry of one boolean without writing it anywhere.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The result entity.</param>
    /// <param name="operation">Which boolean.</param>
    /// <returns>The mesh, or <see langword="null" /> when there is nothing left of it.</returns>
    public static EditMesh? Combine(SceneDocument document, Entity entity, BooleanOperation operation) {
        ArgumentNullException.ThrowIfNull(document);

        EditMesh? result = null;

        foreach (var operand in Operands(document, entity)) {
            if (document.MeshOf(operand) is not { } mesh) {
                continue;
            }

            var relative = Relative(document, entity, operand);

            if (result is null) {
                result = new();

                MeshOperations.Append(result, mesh, relative);

                continue;
            }

            result = MeshBoolean.Apply(result, mesh, operation, relative);

            if (result is null) {
                return null;
            }
        }

        return result is null || result.IsEmpty ? null : result;
    }

    /// <summary>The entities a boolean reads, in the order the file lists them.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The result entity.</param>
    /// <returns>Its children.</returns>
    /// <remarks>
    ///     ⚠ <b>Order matters and it is the outliner's.</b> A difference is not commutative — the
    ///     first operand is the one being cut — so "reorder these two rows" has to be the way a
    ///     designer swaps which is which, rather than a field somewhere saying so a second time.
    /// </remarks>
    public static IReadOnlyList<Entity> Operands(SceneDocument document, Entity entity) {
        ArgumentNullException.ThrowIfNull(document);

        List<Entity> operands = [];

        foreach (var child in Hierarchy.ChildrenOf(document.World, entity)) {
            operands.Add(child);
        }

        return operands;
    }

    /// <summary>One number that moves whenever any operand's geometry or placement does.</summary>
    static int Fingerprint(SceneDocument document, Entity entity) {
        var hash = new HashCode();

        foreach (var operand in Operands(document, entity)) {
            hash.Add(operand);
            hash.Add(document.MeshVersion(operand));

            // ⚠ The transform as well as the mesh, because moving a cutter changes the result without
            // changing a single vertex of it — which is most of what dragging a boolean's operand is.
            if (document.World.Has<LocalTransform>(operand)) {
                var local = document.World.Read<LocalTransform>(operand);

                hash.Add(local.Position);
                hash.Add(local.Rotation);
                hash.Add(local.Scale);
            }
        }

        return hash.ToHashCode();
    }

    static Matrix4x4 Relative(SceneDocument document, Entity parent, Entity child) {
        var world = document.World;

        if (!world.Has<WorldTransform>(parent) || !world.Has<WorldTransform>(child)) {
            return world.Has<LocalTransform>(child) ? world.Read<LocalTransform>(child).ToMatrix() : Matrix4x4.Identity;
        }

        return Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)
            ? world.Read<WorldTransform>(child).Value * inverse
            : Matrix4x4.Identity;
    }

    static int Depth(SceneDocument document, Entity entity) {
        var depth = 0;

        for (var at = Hierarchy.ParentOf(document.World, entity); !at.IsNull; at = Hierarchy.ParentOf(document.World, at)) {
            depth++;
        }

        return depth;
    }
}

/// <summary>Making an entity a boolean of its operands, or collapsing one, undoably.</summary>
/// <remarks>
///     ⚠ <b>Records the node and not the geometry.</b> The result is a function of the operands, so
///     putting the node back is putting the mesh back — and the operands are entities, which the
///     create and reparent commands already know how to restore. What this holds is one enum and one
///     integer either side of the change.
/// </remarks>
public sealed class BooleanCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly Entity entity;
    readonly CsgNode? before;
    readonly CsgNode? after;

    /// <summary>Describes giving an entity a boolean, or taking one away.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The result entity.</param>
    /// <param name="after">The node it should have, or <see langword="null" /> to collapse it.</param>
    /// <param name="name">What the history calls it.</param>
    public BooleanCommand(SceneDocument document, Entity entity, CsgNode? after, string name = "Boolean") {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        this.entity = entity;
        this.after = after;

        before = document.BooleanOf(entity);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(after);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(before);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>⚠ <b>Two of these never merge.</b> Making a boolean and collapsing one are decisions
    ///     rather than frames of a drag, and a designer who did both is entitled to step back over
    ///     each.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    void Apply(CsgNode? node) {
        document.SetBoolean(entity, node);

        // ⚠ Collapsing leaves the geometry where it is and re-establishing rebuilds it. That
        // asymmetry is the same one the shape demotion has, and for the same reason: what a boolean
        // owns is the derivation, and the mesh outlives it.
        if (node is not null) {
            SceneCsg.Evaluate(document, entity);
        }
    }
}
