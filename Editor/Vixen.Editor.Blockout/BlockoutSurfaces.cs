// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's Surfaces table, run against a scene and put on the undo stack.</summary>
/// <remarks>
///     <para>
///         <b>What a face is made of and how it is shaded, as against what shape it is.</b> The
///         arithmetic is <see cref="MeshSurfaces" />'; the material assignment is the document's,
///         because an <c>AssetReference</c> means nothing to a geometry kernel.
///     </para>
///     <para>
///         ⚠ <b>A material is assigned to a face's <i>group</i> rather than to the face.</b> That is
///         D2's whole reason for having groups: a wall's twelve faces after two bevels are still one
///         wall, and an assignment remembered per face index is one that the next loop cut renumbers
///         out from under. Selecting one face of a wall and assigning brick makes the whole wall brick
///         — which is what somebody who selected a wall meant, and is why "select coplanar" and
///         "select group" are one keystroke each.
///     </para>
///     <para>
///         ⚠ <b>Mapping and smoothing demote a parametric shape and assigning a material does
///         not.</b> A projection writes into the mesh's corner layer, so a shape that stayed parametric
///         would lose it the next time anybody nudged a parameter. A material assignment is on the
///         document beside the mesh and survives a regeneration untouched — so a designer can dress a
///         parametric corridor and still widen it.
///     </para>
/// </remarks>
public static class BlockoutSurfaces {
    /// <summary>Assigns a material to every group the selection touches.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="material">The material, or <see cref="AssetReference.Null" /> to clear it.</param>
    /// <returns>How many groups were assigned.</returns>
    public static int Assign(MeshEdit editing, AssetReference material) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh) {
            return 0;
        }

        var faces = editing.Selection.Converted(mesh, MeshElementKind.Face);

        if (faces.Count == 0) {
            return 0;
        }

        SortedSet<int> groups = [];

        foreach (var face in faces) {
            groups.Add(mesh.Faces[face].Group);
        }

        var document = editing.Document;

        using (document.Stack.BeginTransaction("Assign Material")) {
            foreach (var group in groups) {
                document.Stack.Execute(
                    new MaterialCommand(document, editing.Target, group, document.MaterialsOf(editing.Target).GetValueOrDefault(group, AssetReference.Null), material)
                );
            }
        }

        return groups.Count;
    }

    /// <summary>Puts the selected faces in a group of their own, so a material can be given to them.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether anything was regrouped.</returns>
    /// <remarks>
    ///     ⚠ <b>The step between "these faces" and "this material", and it has to be explicit.</b>
    ///     A generator's groups are the ones a designer wants nine times in ten — the treads of a
    ///     staircase, the reveal of a doorway — and the tenth is a wall somebody has decided is two
    ///     walls. Splitting silently on assignment would make every material assignment a change to
    ///     the mesh's structure, which is a much larger thing than it looks.
    /// </remarks>
    public static bool Regroup(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var faces = editing.Selection.Converted(mesh, MeshElementKind.Face);

        if (faces.Count == 0) {
            return false;
        }

        var group = 0;

        foreach (var face in mesh.Faces) {
            group = Math.Max(group, face.Group + 1);
        }

        var was = new EditMesh(mesh);

        foreach (var face in faces) {
            mesh.SetGroup(face, group);
        }

        Record(editing, was, "New Face Group");
        return true;
    }

    /// <summary>Projects texture coordinates onto the selected faces.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="projection">Which projection.</param>
    /// <param name="scale">How many world units one repeat covers.</param>
    /// <returns>Whether anything was mapped.</returns>
    /// <remarks>⚠ <b>World is the default and doc 24's P5 says why:</b> a block-out box scaled 8×3
    ///     must not stretch its texels. The entity's world matrix is what makes that true across two
    ///     objects of different scales, so it is read here rather than left to the caller.</remarks>
    public static bool Project(
        MeshEdit editing,
        UvProjection projection = UvProjection.World,
        float scale = MeshSurfaces.DefaultScale
    ) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var faces = Faces(editing, mesh);
        var was = new EditMesh(mesh);

        if (MeshSurfaces.Project(mesh, faces, projection, scale, ToWorld(editing)) == 0) {
            return false;
        }

        Record(editing, was, "Project UVs");
        return true;
    }

    /// <summary>Moves, turns and scales the selected faces' coordinates.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="offset">How far to slide them, in repeats.</param>
    /// <param name="rotation">How far to turn them, in radians.</param>
    /// <param name="scale">How much to scale them by.</param>
    /// <returns>Whether anything moved.</returns>
    public static bool Transform(
        MeshEdit editing,
        Vector2 offset = default,
        float rotation = 0f,
        Vector2 scale = default
    ) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var was = new EditMesh(mesh);

        if (MeshSurfaces.Transform(mesh, Faces(editing, mesh), offset, rotation, scale) == 0) {
            return false;
        }

        Record(editing, was, "Transform UVs");
        return true;
    }

    /// <summary>Stretches the selected faces' coordinates to cover exactly one repeat each.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <returns>Whether anything was fitted.</returns>
    public static bool Fit(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var was = new EditMesh(mesh);

        if (MeshSurfaces.Fit(mesh, Faces(editing, mesh)) == 0) {
            return false;
        }

        Record(editing, was, "Fit UVs");
        return true;
    }

    /// <summary>Puts the selected faces in a smoothing group, or takes them out of one.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="smooth">Whether they are smoothed together, or hardened.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>Doc 24's own words: a hard edge is the absence of a smoothing group, so hardening is
    ///     the same verb with a group of zero.</remarks>
    public static bool Smooth(MeshEdit editing, bool smooth = true) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var group = 0;

        if (smooth) {
            foreach (var face in mesh.Faces) {
                group = Math.Max(group, face.Smoothing);
            }

            group++;
        }

        var was = new EditMesh(mesh);

        if (MeshSurfaces.Smooth(mesh, Faces(editing, mesh), group) == 0) {
            return false;
        }

        Record(editing, was, smooth ? "Smooth Faces" : "Harden Faces");
        return true;
    }

    /// <summary>Groups the whole mesh's faces by how sharply they meet.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="angle">How far two faces may turn and still be one surface, in radians.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>The verb a converted cylinder wants, and the one that stops a block-out's curves
    ///     reading as polygons — see <see cref="MeshSurfaces.AutoSmooth" />.</remarks>
    public static bool AutoSmooth(MeshEdit editing, float angle = MeshSurfaces.DefaultSmoothingAngle) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || !editing.Demote()) {
            return false;
        }

        var was = new EditMesh(mesh);

        MeshSurfaces.AutoSmooth(mesh, angle);
        Record(editing, was, "Auto Smooth");

        return true;
    }

    /// <summary>Which faces a surface verb acts on: the selection, or all of them when it is empty.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty means everything here and means nothing in <see cref="BlockoutGeometry" />, and
    ///     the asymmetry is deliberate.</b> "Project the UVs" with nothing selected obviously means the
    ///     whole object; "extrude" with nothing selected obviously means nothing. A surface verb has a
    ///     sensible whole-object reading and a geometry verb does not.
    /// </remarks>
    static IReadOnlyCollection<int>? Faces(MeshEdit editing, EditMesh mesh) {
        if (editing.Selection.IsEmpty) {
            return null;
        }

        var faces = editing.Selection.Converted(mesh, MeshElementKind.Face);

        return faces.Count > 0 ? [.. faces] : null;
    }

    static Matrix4x4 ToWorld(MeshEdit editing) {
        var world = editing.Document.World;

        return !editing.Target.IsNull && world.Has<WorldTransform>(editing.Target)
            ? world.Read<WorldTransform>(editing.Target).Value
            : Matrix4x4.Identity;
    }

    static void Record(MeshEdit editing, EditMesh was, string name) {
        var document = editing.Document;

        document.TouchMesh(editing.Target);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, editing.Target, was, name));

        editing.Reconcile();
    }
}
