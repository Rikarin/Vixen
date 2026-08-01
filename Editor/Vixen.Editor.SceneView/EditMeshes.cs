// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core.Scenes;
using Vixen.Geometry;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>The six lines that join the mesh kernel to the things that draw and save one.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D1 puts these here rather than in the kernel, and the reason is one
///         reference.</b> <c>Core/Vixen.Geometry</c> depends on <c>Vixen.Core.Mathematics</c> and
///         nothing else — a geometry kernel that needed the render assembly to describe a triangle
///         would be backwards, and one that needed the editor's file format to describe a face would
///         be worse. So the kernel hands back its own arrays and the copies live here, beside the code
///         that uploads and writes them. <c>Vixen.Navigation</c> makes exactly this choice and it has
///         cost it nothing.
///     </para>
///     <para>
///         ⚠ <b>A drawn vertex is a corner and not a position, which is why the trip out is not the
///         trip in reversed.</b> Going in, twenty-four entries weld to eight positions. Coming out,
///         eight positions expand to one vertex per corner again — because a normal belongs to a
///         corner, and a cube drawn from eight shared vertices is a cube lit as a very lumpy sphere.
///     </para>
/// </remarks>
public static class EditMeshes {
    /// <summary>Builds a mesh from geometry a renderer would draw.</summary>
    /// <param name="mesh">The geometry.</param>
    /// <param name="weld">How near two positions may be and still be one, as a fraction of the bounds.</param>
    /// <returns>The mesh.</returns>
    public static EditMesh From(MeshData mesh, float weld = EditMesh.DefaultWeldTolerance) {
        ArgumentNullException.ThrowIfNull(mesh);
        return EditMesh.FromTriangles(mesh.Positions, mesh.Indices, weld);
    }

    /// <summary>Builds one from a primitive.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="segments">How many divisions around its axis.</param>
    /// <param name="rings">How many along it.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     What "make this cube editable" does, and what doc 24's P4 shape tool will do at the moment
    ///     a parametric shape is demoted to a plain mesh.
    /// </remarks>
    public static EditMesh From(
        PrimitiveKind kind,
        int segments = MeshPrimitives.DefaultSegments,
        int rings = MeshPrimitives.DefaultRings
    ) =>
        From(MeshPrimitives.Create(kind, segments, rings));

    /// <summary>Turns a mesh into geometry a renderer can draw.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The geometry.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One vertex per corner, with the face's own normal — so a converted mesh is flat
    ///         shaded.</b> That is right for a block-out and it is not right for a converted sphere,
    ///         which comes out faceted. Smoothing groups are what fix it and they are doc 24's P5;
    ///         saying so here is what stops it being discovered as a rendering bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mesh's own corner layers win where it has them.</b> A mesh that came from a
    ///         primitive and was never edited still carries the normals it arrived with, so the trip
    ///         out is lossless for the case that matters most — the cube that has not been touched yet.
    ///     </para>
    /// </remarks>
    public static MeshData ToMeshData(this EditMesh mesh, string name = "Mesh") {
        ArgumentNullException.ThrowIfNull(mesh);

        var triangles = 0;

        foreach (var face in mesh.Faces) {
            triangles += Math.Max(face.Count - 2, 0);
        }

        var positions = new Vector3[mesh.CornerCount];
        var normals = new Vector3[mesh.CornerCount];
        var texCoords = new Vector2[mesh.CornerCount];
        var indices = new int[triangles * 3];

        var carried = mesh.Normals;
        var mapped = mesh.TexCoords;
        var at = 0;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var facing = mesh.Normal(face);

            for (var corner = 0; corner < entry.Count; corner++) {
                var index = entry.Start + corner;

                positions[index] = mesh.Positions[mesh.Corners[index]];
                normals[index] = carried.IsEmpty ? facing : carried[index];
                texCoords[index] = mapped.IsEmpty ? Vector2.Zero : mapped[index];
            }

            // The same fan `EditMesh.Triangulate` produces, over corner indices rather than position
            // indices — which is the whole difference between the drawing structure and the kernel's.
            for (var corner = 1; corner + 1 < entry.Count; corner++) {
                indices[at++] = entry.Start;
                indices[at++] = entry.Start + corner;
                indices[at++] = entry.Start + corner + 1;
            }
        }

        var bounds = mesh.Bounds;

        return new MeshData {
            Name = name,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = indices,
            Bounds = bounds
        };
    }

    /// <summary>Turns a mesh into what a scene file carries.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The record.</returns>
    public static SceneMeshData ToSceneData(this EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var data = new SceneMeshData();

        foreach (var position in mesh.Positions) {
            data.Positions.Add(position);
        }

        foreach (var corner in mesh.Corners) {
            data.Corners.Add(corner);
        }

        foreach (var face in mesh.Faces) {
            data.Faces.Add(face.Count);
            data.Groups.Add(face.Group);
        }

        return data;
    }

    /// <summary>Rebuilds a mesh from what a scene file carried.</summary>
    /// <param name="data">The record.</param>
    /// <returns>The mesh, or <see langword="null" /> when the record says there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>A record that does not add up produces the faces that do rather than throwing.</b> A
    ///     scene is a file people hand-edit and merge, and an editor that refused to open one because
    ///     a face count ran off the end of the corner list would lose the ninety-nine entities that
    ///     were fine. The face that could not be read is dropped, which is visible; the scene opening
    ///     is what lets somebody fix it.
    /// </remarks>
    public static EditMesh? FromSceneData(SceneMeshData? data) {
        if (data is null || data.Faces.Count == 0) {
            return null;
        }

        var mesh = new EditMesh();

        foreach (var position in data.Positions) {
            mesh.AddPosition(position);
        }

        var at = 0;

        for (var face = 0; face < data.Faces.Count; face++) {
            var count = data.Faces[face];

            if (count < 3 || at + count > data.Corners.Count) {
                break;
            }

            var loop = new int[count];
            var valid = true;

            for (var corner = 0; corner < count; corner++) {
                loop[corner] = data.Corners[at + corner];
                valid &= (uint) loop[corner] < (uint) data.Positions.Count;
            }

            if (valid) {
                mesh.AddFace(loop, face < data.Groups.Count ? data.Groups[face] : 0);
            }

            at += count;
        }

        return mesh.IsEmpty ? null : mesh;
    }
}
