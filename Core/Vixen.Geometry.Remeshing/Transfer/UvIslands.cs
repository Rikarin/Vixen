// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which UV island each face of a mesh is in.</summary>
/// <remarks>
///     <para>
///         <b>An island is the faces joined by edges whose coordinates agree on both sides.</b> Two faces sharing an edge
///         in space but not in UV meet at a seam, and are in different islands even when nothing else separates them.
///     </para>
///     <para>
///         ⚠ <b>Numbered in face order</b>: the island of face zero is island zero, and each later island takes the
///         next number when its first face is reached. So the numbers, and the colours a map draws from them, depend
///         on the mesh alone and are the same in every bake of it.
///     </para>
/// </remarks>
static class UvIslands {
    /// <summary>How far apart two coordinates may be and still be one corner of the layout.</summary>
    /// <remarks>A millionth of the unit square, far below a texel of any atlas and above a file's rounding.</remarks>
    const float Tolerance = 1e-6f;

    /// <summary>The island of every face.</summary>
    /// <param name="mesh">A mesh with texture coordinates.</param>
    /// <returns>An island per face, numbered from zero in face order; every face is island zero with no coordinates.</returns>
    public static int[] Of(EditMesh mesh) {
        var islands = new int[mesh.FaceCount];

        if (mesh.TexCoords.Length != mesh.CornerCount) {
            return islands;
        }

        var parents = new int[mesh.FaceCount];

        for (var face = 0; face < parents.Length; face++) {
            parents[face] = face;
        }

        var edges = mesh.Edges;

        for (var edge = 0; edge < edges.Count; edge++) {
            var faces = mesh.FacesOf(edge);
            var (a, b) = (edges[edge].A, edges[edge].B);

            for (var other = 1; other < faces.Length; other++) {
                if (Agree(mesh, faces[0], faces[other], a) && Agree(mesh, faces[0], faces[other], b)) {
                    Join(parents, faces[0], faces[other]);
                }
            }
        }

        Dictionary<int, int> numbers = [];

        for (var face = 0; face < islands.Length; face++) {
            var root = Root(parents, face);

            if (!numbers.TryGetValue(root, out var number)) {
                number = numbers.Count;
                numbers.Add(root, number);
            }

            islands[face] = number;
        }

        return islands;
    }

    /// <summary>Whether two faces put a position at the same place in the layout.</summary>
    static bool Agree(EditMesh mesh, int one, int two, int position) =>
        Vector2.DistanceSquared(Coordinate(mesh, one, position), Coordinate(mesh, two, position))
        <= Tolerance * Tolerance;

    /// <summary>The coordinate a face's corner on a position carries.</summary>
    static Vector2 Coordinate(EditMesh mesh, int face, int position) {
        var loop = mesh.CornersOf(face);

        for (var at = 0; at < loop.Length; at++) {
            if (loop[at] == position) {
                return mesh.TexCoords[mesh.Faces[face].Start + at];
            }
        }

        return new(float.NaN);
    }

    static int Root(int[] parents, int face) {
        while (parents[face] != face) {
            parents[face] = parents[parents[face]];
            face = parents[face];
        }

        return face;
    }

    static void Join(int[] parents, int one, int two) {
        var (first, second) = (Root(parents, one), Root(parents, two));

        if (first != second) {
            parents[Math.Max(first, second)] = Math.Min(first, second);
        }
    }
}
