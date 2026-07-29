// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Meshes with a known answer, built here rather than checked in.</summary>
/// <remarks>
///     A sphere is the fixture that matters: it is closed, it is smooth, every vertex is at a known
///     distance from a known centre, and a simplification of it can therefore be measured against the
///     surface it is approximating rather than against a picture of one. A grid is the other half —
///     open, so it has a boundary the build must not move, and flat, so the exact error of any
///     simplification of it is zero.
/// </remarks>
static class Shapes {
    /// <summary>A flat grid on the XZ plane, one unit across.</summary>
    /// <param name="cells">How many cells along each axis.</param>
    /// <returns>The mesh.</returns>
    public static MeshletBuildInput Grid(int cells) {
        var positions = new Vector3[(cells + 1) * (cells + 1)];
        var indices = new int[cells * cells * 6];

        for (var z = 0; z <= cells; z++) {
            for (var x = 0; x <= cells; x++) {
                positions[(z * (cells + 1)) + x] = new((float)x / cells, 0f, (float)z / cells);
            }
        }

        var cursor = 0;

        for (var z = 0; z < cells; z++) {
            for (var x = 0; x < cells; x++) {
                var corner = (z * (cells + 1)) + x;

                indices[cursor++] = corner;
                indices[cursor++] = corner + cells + 1;
                indices[cursor++] = corner + 1;

                indices[cursor++] = corner + 1;
                indices[cursor++] = corner + cells + 1;
                indices[cursor++] = corner + cells + 2;
            }
        }

        return new() { Positions = positions, Indices = indices };
    }

    /// <summary>An icosphere of unit radius, centred on the origin.</summary>
    /// <param name="subdivisions">How many times to split every triangle into four.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     An icosphere rather than a sphere of latitudes and longitudes: the latter has poles where
    ///     hundreds of triangles meet at one vertex and a seam of doubled vertices down one side, and
    ///     both are the degenerate cases rather than the ordinary one. Every vertex here is shared
    ///     exactly, so the mesh has no seams and welding is a no-op — which is what makes it the
    ///     fixture that isolates the DAG from the topology.
    /// </remarks>
    public static MeshletBuildInput Sphere(int subdivisions) {
        var golden = (1f + MathF.Sqrt(5f)) / 2f;

        var positions = new List<Vector3> {
            new(-1, golden, 0), new(1, golden, 0), new(-1, -golden, 0), new(1, -golden, 0),
            new(0, -1, golden), new(0, 1, golden), new(0, -1, -golden), new(0, 1, -golden),
            new(golden, 0, -1), new(golden, 0, 1), new(-golden, 0, -1), new(-golden, 0, 1)
        };

        for (var index = 0; index < positions.Count; index++) {
            positions[index] = Vector3.Normalize(positions[index]);
        }

        int[] faces = [
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
        ];

        var indices = faces.ToList();

        for (var pass = 0; pass < subdivisions; pass++) {
            var midpoints = new Dictionary<long, int>();
            var split = new List<int>(indices.Count * 4);

            for (var triangle = 0; triangle < indices.Count / 3; triangle++) {
                var a = indices[triangle * 3];
                var b = indices[(triangle * 3) + 1];
                var c = indices[(triangle * 3) + 2];

                var ab = Midpoint(positions, midpoints, a, b);
                var bc = Midpoint(positions, midpoints, b, c);
                var ca = Midpoint(positions, midpoints, c, a);

                split.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
            }

            indices = split;
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    /// <summary>The vertex halfway along an edge, on the unit sphere, created once per edge.</summary>
    /// <param name="positions">The vertices so far.</param>
    /// <param name="midpoints">Which edges already have one.</param>
    /// <param name="left">One end.</param>
    /// <param name="right">The other.</param>
    /// <returns>The midpoint's index.</returns>
    static int Midpoint(List<Vector3> positions, Dictionary<long, int> midpoints, int left, int right) {
        var key = left < right ? ((long)left << 32) | (uint)right : ((long)right << 32) | (uint)left;

        if (midpoints.TryGetValue(key, out var existing)) {
            return existing;
        }

        positions.Add(Vector3.Normalize((positions[left] + positions[right]) * 0.5f));
        midpoints[key] = positions.Count - 1;

        return positions.Count - 1;
    }
}
