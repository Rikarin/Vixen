// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's geometry verbs, run against a scene and put on the undo stack.</summary>
/// <remarks>
///     <para>
///         <b>The other side of <see cref="BlockoutSelection" />, and it holds nothing either.</b> The
///         arithmetic is <see cref="MeshOperations" />'; what is here is the three things every verb
///         needs and none of them has: which faces the current element mode means, the copy taken
///         before the change, and what stays selected afterwards.
///     </para>
///     <para>
///         ⚠ <b>Every one of them is one entry in the history and it records the whole mesh.</b> Doc
///         24's D3: a topology change has no inverse to record, and an undo implemented as an inverse
///         operation is a second implementation of every verb that will disagree with the first — see
///         <c>EditMeshCommand.Rebuilt</c>, which is what each of these pushes.
///     </para>
///     <para>
///         ⚠ <b>What is selected afterwards is what the verb made.</b> Extruding a face and then
///         moving it is one gesture in every modelling tool there is; a verb that left the original
///         selection would make the second half of that gesture act on the geometry the first half
///         left behind.
///     </para>
///     <para>
///         ⚠ <b>Amounts are in the mesh's own space, which is not the scene's when the entity is
///         scaled.</b> A caller working from a pointer drag has to take its distance through the
///         entity's inverse first — <see cref="Local" /> is that, and it is here rather than in each
///         verb because the mistake it prevents is invisible until somebody extrudes a wall that had
///         been scaled to half.
///     </para>
/// </remarks>
public static class BlockoutGeometry {
    /// <summary>Pulls the selected faces out along their normal.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="distance">How far, in the mesh's own space.</param>
    /// <param name="individually">Whether each face goes on its own rather than as one region.</param>
    /// <returns>Whether anything was extruded.</returns>
    public static bool Extrude(MeshEdit editing, float distance, bool individually = false) =>
        Run(
            editing,
            individually ? "Extrude Individual Faces" : "Extrude",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.Extrude(mesh, faces, distance, individually),
            MeshElementKind.Face
        );

    /// <summary>Pulls them along a direction instead.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="offset">How far and which way, in the mesh's own space.</param>
    /// <returns>Whether anything was extruded.</returns>
    /// <remarks>Doc 24's "offset elements": <c>Ctrl</c>+drag moves along the <i>normal</i> rather than
    ///     along an axis, and this is the same verb with the direction the drag produced.</remarks>
    public static bool ExtrudeAlong(MeshEdit editing, Vector3 offset) =>
        Run(
            editing,
            "Extrude",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.ExtrudeAlong(mesh, faces, offset),
            MeshElementKind.Face
        );

    /// <summary>Shrinks the selected faces towards their own centre.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="amount">How far in, in the mesh's own space.</param>
    /// <param name="individually">Whether each face is inset on its own.</param>
    /// <returns>Whether anything was inset.</returns>
    public static bool Inset(MeshEdit editing, float amount, bool individually = false) =>
        Run(
            editing,
            individually ? "Inset Individual Faces" : "Inset",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.Inset(mesh, faces, amount, individually),
            MeshElementKind.Face
        );

    /// <summary>Cuts the corner off the selected edges.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="width">How far back from each edge, in the mesh's own space.</param>
    /// <param name="segments">How many faces across the bevel.</param>
    /// <param name="unresolved">How many corners the bevel could not resolve.</param>
    /// <returns>Whether anything was bevelled.</returns>
    /// <remarks>
    ///     ⚠ <b>The unresolved count is handed back rather than logged, and the caller is expected to
    ///     say so.</b> Doc 24 is explicit that the honest first version reports where it could not
    ///     resolve a corner instead of producing a self-intersecting one silently — a number nobody
    ///     surfaces is the same as not having it.
    /// </remarks>
    public static bool Bevel(MeshEdit editing, float width, int segments, out int unresolved) {
        var count = 0;

        var done = Run(
            editing,
            "Bevel",
            MeshElementKind.Edge,
            (mesh, edges) => {
                var made = MeshOperations.Bevel(mesh, edges, width, segments, out var reported);

                count = reported;
                return made;
            },
            MeshElementKind.Face
        );

        unresolved = count;
        return done;
    }

    /// <summary>Puts loops across the ring the active edge is part of.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="cuts">How many loops.</param>
    /// <param name="slide">Where a single cut sits along the ring, from 0 to 1.</param>
    /// <returns>Whether anything was cut.</returns>
    public static bool LoopCut(MeshEdit editing, int cuts = 1, float slide = 0.5f) =>
        Run(
            editing,
            "Loop Cut",
            MeshElementKind.Edge,
            (mesh, edges) => edges.Count == 0 ? [] : MeshOperations.LoopCut(mesh, edges[^1], cuts, slide),
            MeshElementKind.Face
        );

    /// <summary>Splits the selected faces into one face per corner.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="count">How many times.</param>
    /// <returns>Whether anything was subdivided.</returns>
    public static bool Subdivide(MeshEdit editing, int count = 1) =>
        Run(
            editing,
            "Subdivide",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.Subdivide(mesh, faces, count),
            MeshElementKind.Face
        );

    /// <summary>Joins the two selected faces with a tube.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether they were bridged.</returns>
    /// <remarks>Exactly two, because a bridge is a statement about a pair — three faces is three
    ///     pairings and no rule for choosing between them is right for every shape.</remarks>
    public static bool Bridge(MeshEdit editing) =>
        Run(
            editing,
            "Bridge",
            MeshElementKind.Face,
            (mesh, faces) => faces.Count == 2 ? MeshOperations.Bridge(mesh, faces[0], faces[1]) : [],
            MeshElementKind.Face
        );

    /// <summary>Puts a face across the hole the selected edges are on the rim of.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether a hole was filled.</returns>
    public static bool FillHole(MeshEdit editing) =>
        Run(
            editing,
            "Fill Hole",
            MeshElementKind.Edge,
            (mesh, edges) => {
                foreach (var edge in edges) {
                    var made = MeshOperations.FillHole(mesh, edge);

                    if (made >= 0) {
                        return [made];
                    }
                }

                return [];
            },
            MeshElementKind.Face
        );

    /// <summary>Turns the selected faces inside out.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether anything was flipped.</returns>
    public static bool Flip(MeshEdit editing) =>
        Run(
            editing,
            "Flip Normals",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.Flip(mesh, faces) > 0 ? faces : [],
            MeshElementKind.Face
        );

    /// <summary>Merges the selected positions into one.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="at">Where the merged position goes, or null for their average.</param>
    /// <returns>Whether anything was welded.</returns>
    /// <remarks>Doc 24's <c>M ⋯</c>: to centre is the default, to last is
    ///     <see cref="MeshSelection.Active" />'s position, and to the cursor is a point the caller
    ///     supplies.</remarks>
    public static bool Weld(MeshEdit editing, Vector3? at = null) =>
        Run(
            editing,
            "Weld",
            MeshElementKind.Vertex,
            (mesh, positions) => MeshOperations.Weld(mesh, positions, at) > 0 ? [0] : [],
            MeshElementKind.Vertex
        );

    /// <summary>Merges every pair of positions nearer than a distance.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="distance">How near counts, in the mesh's own space.</param>
    /// <returns>Whether anything was merged.</returns>
    public static bool MergeByDistance(MeshEdit editing, float distance) =>
        Run(
            editing,
            "Merge by Distance",
            MeshElementKind.Vertex,
            (mesh, _) => MeshOperations.MergeByDistance(mesh, distance) > 0 ? [0] : [],
            MeshElementKind.Vertex
        );

    /// <summary>Removes the selected edges and keeps the surface.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether anything was dissolved.</returns>
    /// <remarks>Doc 24's distinction in one sentence: dissolve removes an element and keeps the
    ///     surface, delete makes a hole.</remarks>
    public static bool Dissolve(MeshEdit editing) =>
        Run(
            editing,
            "Dissolve Edges",
            MeshElementKind.Edge,
            (mesh, edges) => MeshOperations.Dissolve(mesh, edges) > 0 ? [] : [],
            MeshElementKind.Face,
            allowEmptyResult: true
        );

    /// <summary>Removes the selected faces, leaving a hole.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether anything was deleted.</returns>
    public static bool Delete(MeshEdit editing) =>
        Run(
            editing,
            "Delete Faces",
            MeshElementKind.Face,
            (mesh, faces) => MeshOperations.Delete(mesh, faces) > 0 ? [] : [],
            MeshElementKind.Face,
            allowEmptyResult: true
        );

    /// <summary>Takes the selected faces out into an entity of their own.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="name">What to call the new entity.</param>
    /// <param name="keep">Whether the originals stay, which makes this a copy.</param>
    /// <returns>The new entity, or null when nothing was detached.</returns>
    /// <remarks>
    ///     ⚠ <b>Two undoable changes in one transaction, because it is one act.</b> An entity arrives
    ///     and a mesh loses faces; undoing one without the other is a scene with the same geometry
    ///     twice or with none of it, and <c>CommandStack</c>'s transactions exist for exactly this.
    /// </remarks>
    public static Vixen.Core.Entity? Detach(MeshEdit editing, string name = "Mesh", bool keep = false) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || editing.Selection.IsEmpty) {
            return null;
        }

        var faces = editing.Selection.Converted(mesh, MeshElementKind.Face);

        if (faces.Count == 0) {
            return null;
        }

        var document = editing.Document;
        var was = new EditMesh(mesh);
        var target = editing.Target;

        var taken = MeshOperations.Detach(mesh, faces, keep);

        if (taken is null) {
            return null;
        }

        var stack = document.Stack;
        var label = keep ? "Duplicate Faces" : "Detach Faces";

        Vixen.Core.Entity entity;

        using (stack.BeginTransaction(label)) {
            entity = document.Create(name, Placement(document, target));

            document.SetMesh(entity, taken);
            stack.Execute(EditMeshCommand.Rebuilt(document, entity, null, label));

            document.TouchMesh(target);
            stack.Execute(EditMeshCommand.Rebuilt(document, target, was, label));
        }

        editing.Reconcile();
        return entity;
    }

    /// <summary>Puts another entity's mesh into the one being edited, and deletes it.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="others">The entities to absorb.</param>
    /// <returns>How many were merged in.</returns>
    /// <remarks>The inverse of <see cref="Detach" />, and doc 24's own words for what it is for: this
    ///     is what makes a room one mesh before baking.</remarks>
    public static int Merge(MeshEdit editing, IReadOnlyCollection<Vixen.Core.Entity> others) {
        ArgumentNullException.ThrowIfNull(editing);
        ArgumentNullException.ThrowIfNull(others);

        if (!editing.IsActive || editing.Mesh is not { } mesh) {
            return 0;
        }

        var document = editing.Document;
        var target = editing.Target;
        var was = new EditMesh(mesh);
        var merged = 0;

        var stack = document.Stack;

        using (stack.BeginTransaction("Merge Meshes")) {
            foreach (var other in others) {
                if (other == target || document.MeshOf(other) is not { } geometry) {
                    continue;
                }

                // Into the target's space, which is what keeps two walls a metre apart a metre apart.
                MeshOperations.Append(mesh, geometry, Relative(document, target, other));
                document.Delete([other]);

                merged++;
            }

            if (merged > 0) {
                document.TouchMesh(target);
                stack.Execute(EditMeshCommand.Rebuilt(document, target, was, "Merge Meshes"));
            }
        }

        editing.Reconcile();
        return merged;
    }

    /// <summary>Takes a distance in the scene into the mesh's own space.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="offset">The distance, in world units.</param>
    /// <returns>The same distance in the mesh's space.</returns>
    /// <remarks>
    ///     ⚠ <b>A direction rather than a point, so the translation is not applied.</b> An extrude of
    ///     one metre on an entity scaled to a half is two units in the mesh, and a verb given the
    ///     world number would move it half as far as the pointer said.
    /// </remarks>
    public static Vector3 Local(MeshEdit editing, Vector3 offset) {
        ArgumentNullException.ThrowIfNull(editing);

        var world = editing.Document.World;

        if (editing.Target.IsNull || !world.Has<WorldTransform>(editing.Target)) {
            return offset;
        }

        return Matrix4x4.Invert(world.Read<WorldTransform>(editing.Target).Value, out var inverse)
            ? Matrix4x4.TransformDirection(offset, inverse)
            : offset;
    }

    /// <summary>Runs one verb: copy, change, record, reselect.</summary>
    /// <remarks>
    ///     ⚠ <b>The reconcile happens between the change and the reselection, and the order is the
    ///     whole of why this is one helper.</b> A topology change drops the element selection — see
    ///     <c>MeshEdit.Reconcile</c> — so a verb that set its result first would have it thrown away by
    ///     the next frame's reconcile, and a verb that never reconciled would leave the recorded table
    ///     sizes naming a mesh that no longer exists.
    /// </remarks>
    static bool Run(
        MeshEdit editing,
        string name,
        MeshElementKind wants,
        Func<EditMesh, IReadOnlyList<int>, IReadOnlyList<int>> operation,
        MeshElementKind leaves,
        bool allowEmptyResult = false
    ) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || editing.Selection.IsEmpty) {
            return false;
        }

        var subjects = editing.Selection.Converted(mesh, wants);

        if (subjects.Count == 0) {
            return false;
        }

        var was = new EditMesh(mesh);
        var made = operation(mesh, subjects);

        if (made.Count == 0 && !allowEmptyResult) {
            return false;
        }

        var document = editing.Document;

        document.TouchMesh(editing.Target);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, editing.Target, was, name));

        editing.Reconcile();

        editing.Element = leaves;
        editing.Selection.Set(made);

        return true;
    }

    /// <summary>Where a detached mesh's entity goes: on top of the one it came from.</summary>
    static LocalTransform Placement(SceneDocument document, Vixen.Core.Entity from) {
        if (!document.World.Has<LocalTransform>(from)) {
            return LocalTransform.Identity;
        }

        return document.World.Read<LocalTransform>(from);
    }

    /// <summary>One entity's space as seen from another's.</summary>
    static Matrix4x4 Relative(SceneDocument document, Vixen.Core.Entity target, Vixen.Core.Entity other) {
        var world = document.World;

        if (!world.Has<WorldTransform>(target) || !world.Has<WorldTransform>(other)) {
            return Matrix4x4.Identity;
        }

        return Matrix4x4.Invert(world.Read<WorldTransform>(target).Value, out var inverse)
            ? world.Read<WorldTransform>(other).Value * inverse
            : Matrix4x4.Identity;
    }
}
