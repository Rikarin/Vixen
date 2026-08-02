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
    ///         ⚠ <b>One vertex per corner, and the normals come from the smoothing groups.</b> A face
    ///         with none is flat shaded, which is right for a block-out wall; a face in one takes the
    ///         area-weighted average of the faces at each of its positions that are in the same group,
    ///         which is what stops a converted cylinder coming out as a polygon. That rule is
    ///         <see cref="MeshSurfaces.Normals" />' and is doc 24's P5.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mesh's own corner layers win where it has them.</b> A mesh that came from a
    ///         primitive and was never edited still carries the normals it arrived with, so the trip
    ///         out is lossless for the case that matters most — the cube that has not been touched yet.
    ///     </para>
    /// </remarks>
    public static MeshData ToMeshData(this EditMesh mesh, string name = "Mesh") => mesh.ToMeshData(name, -1);

    /// <summary>Turns one face group of a mesh into geometry a renderer can draw.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">What to call it.</param>
    /// <param name="group">Which face group, or −1 for the whole mesh.</param>
    /// <returns>The geometry.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's P5 per-face material, from the geometry side.</b> A material is per instance
    ///         in the viewport's shader, so two materials on one mesh are two instances of one
    ///         transform over two pieces of geometry — and this is what cuts the pieces.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every corner of the whole mesh is still written, and only the indices are
    ///         filtered.</b> Compacting the vertices per group would mean a position table per piece
    ///         and a map from one numbering to another, for a saving that is a few hundred vertices on
    ///         a wall — and the numbering is the one thing every other consumer of this mesh assumes it
    ///         shares.
    ///     </para>
    /// </remarks>
    public static MeshData ToMeshData(this EditMesh mesh, string name, int group) {
        ArgumentNullException.ThrowIfNull(mesh);

        var triangles = 0;

        foreach (var face in mesh.Faces) {
            if (group < 0 || face.Group == group) {
                triangles += Math.Max(face.Count - 2, 0);
            }
        }

        var positions = new Vector3[mesh.CornerCount];
        var normals = new Vector3[mesh.CornerCount];
        var texCoords = new Vector2[mesh.CornerCount];
        var indices = new int[triangles * 3];

        var carried = mesh.Normals;
        var mapped = mesh.TexCoords;
        var at = 0;

        // ⚠ Computed once for the whole mesh rather than per face, because a smoothed corner's normal
        // depends on every face at its position — which is a question about the mesh and not about the
        // face being written.
        var shaded = carried.IsEmpty ? MeshSurfaces.Normals(mesh) : [];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];

            for (var corner = 0; corner < entry.Count; corner++) {
                var index = entry.Start + corner;

                positions[index] = mesh.Positions[mesh.Corners[index]];
                normals[index] = carried.IsEmpty ? shaded[index] : carried[index];
                texCoords[index] = mapped.IsEmpty ? Vector2.Zero : mapped[index];
            }

            if (group >= 0 && entry.Group != group) {
                continue;
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

        var smoothed = false;

        foreach (var face in mesh.Faces) {
            data.Faces.Add(face.Count);
            data.Groups.Add(face.Group);

            smoothed |= face.Smoothing != 0;
        }

        // ⚠ Only when something set one. A smoothing group of zero is the absence of one, so writing
        // them unconditionally would put a line per face into every block-out scene in the project
        // saying nothing at all — which the face groups are worth and this is not.
        if (smoothed) {
            foreach (var face in mesh.Faces) {
                data.Smoothing.Add(face.Smoothing);
            }
        }

        foreach (var coordinate in mesh.TexCoords) {
            data.TexCoords.Add(coordinate);
        }

        return data;
    }

    /// <summary>Turns a shape's parameters into what a scene file carries.</summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     ⚠ <b>The kind is written as its name and not as its number</b>, the argument
    ///     <c>SceneEntityData.Shape</c> makes at length: an enum written as its integer would put its
    ///     declaration order into every saved scene, so a member inserted in the middle for
    ///     readability would turn every staircase in the project into a sphere, in a diff that shows
    ///     nothing wrong.
    /// </remarks>
    public static SceneShapeData ToSceneData(this ShapeParameters parameters) =>
        new() {
            Kind = parameters.Kind.ToString(),
            Size = parameters.Size,
            Sides = parameters.Sides,
            Steps = parameters.Steps,
            Thickness = parameters.Thickness,
            Inner = parameters.Inner
        };

    /// <summary>Reads a shape's parameters back.</summary>
    /// <param name="data">The record, or null.</param>
    /// <returns>The parameters, or <see langword="null" /> when the record names no shape this build
    ///     knows.</returns>
    /// <remarks>
    ///     ⚠ <b>An unrecognised kind is null and not an exception</b>, for <c>PrimitiveShapes.TryParse</c>'s
    ///     reason: a scene written by a newer editor with a shape this one has never heard of should
    ///     open, minus that entity's geometry, rather than refusing the whole file. The entity keeps
    ///     its transform, its name and its children, so the loss is visible and recoverable by opening
    ///     it in the editor that wrote it.
    /// </remarks>
    public static ShapeParameters? FromSceneData(SceneShapeData? data) {
        if (data is null || !Enum.TryParse<ShapeKind>(data.Kind?.Trim(), ignoreCase: true, out var kind)) {
            return null;
        }

        return new ShapeParameters {
            Kind = kind,
            Size = data.Size,
            Sides = data.Sides,
            Steps = data.Steps,
            Thickness = data.Thickness,
            Inner = data.Inner
        }.Clamped();
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
                mesh.AddFace(
                    loop,
                    face < data.Groups.Count ? data.Groups[face] : 0,
                    face < data.Smoothing.Count ? data.Smoothing[face] : 0
                );
            }

            at += count;
        }

        if (mesh.IsEmpty) {
            return null;
        }

        // ⚠ Only when the file's list is exactly the length the mesh came out at, which a hand-edited
        // or badly merged one need not be. A short layer is what `EditMesh.SetTexCoords` refuses, and
        // a mesh that opened with no mapping is recoverable by projecting it again — where a scene
        // that refused to open is not.
        if (data.TexCoords.Count == mesh.CornerCount) {
            mesh.SetTexCoords(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.TexCoords));
        }

        return mesh;
    }
}
