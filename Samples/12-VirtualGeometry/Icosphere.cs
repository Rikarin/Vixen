// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Samples.VirtualGeometry;

/// <summary>A subdivided icosahedron: the film-resolution mesh this sample imports one of.</summary>
/// <remarks>
///     <para>
///         An icosphere rather than a UV sphere because its triangles are near-uniform, which is the
///         honest input for a clustering pass — a UV sphere's polar fans would hand the builder a
///         degenerate neighbourhood to be clever about and the sample is not about that. Five
///         subdivisions is 20 480 triangles: little by Nanite's standards and more than enough for a
///         DAG several levels deep, whose cut visibly coarsens as the camera pulls away.
///     </para>
///     <para>
///         ⚠ <b>The winding faces out, and it is checked rather than trusted.</b> Every cluster
///         carries a cone of its triangle normals and the traversal culls clusters that face away —
///         so a mesh wound inward is not a shading artefact, it is a mesh the traversal correctly
///         draws none of. Both this sample's sibling fixtures got that wrong once each;
///         <c>VirtualGeometryGoldenTests</c> records it as the first thing the golden image found.
///     </para>
/// </remarks>
static class Icosphere {
    public static (Vector3[] Positions, int[] Indices) Build(int subdivisions) {
        // The icosahedron, from the three orthogonal golden rectangles.
        var t = (1f + MathF.Sqrt(5f)) * 0.5f;

        var positions = new List<Vector3> {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1)
        };

        for (var i = 0; i < positions.Count; i++) {
            positions[i] = Vector3.Normalize(positions[i]);
        }

        List<(int A, int B, int C)> faces = [
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
        ];

        var midpoints = new Dictionary<(int, int), int>();

        for (var level = 0; level < subdivisions; level++) {
            List<(int, int, int)> split = [];

            foreach (var (a, b, c) in faces) {
                var ab = Midpoint(a, b);
                var bc = Midpoint(b, c);
                var ca = Midpoint(c, a);

                split.Add((a, ab, ca));
                split.Add((b, bc, ab));
                split.Add((c, ca, bc));
                split.Add((ab, bc, ca));
            }

            faces = split;
        }

        var indices = new int[faces.Count * 3];

        for (var i = 0; i < faces.Count; i++) {
            var (a, b, c) = faces[i];

            // The outward check: a unit sphere's face normal and its centroid point the same way.
            // Cheap insurance against an edit to the face table above, and the failure it prevents
            // is invisible in any picture that is not empty.
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);

            if (Vector3.Dot(normal, positions[a] + positions[b] + positions[c]) < 0f) {
                (b, c) = (c, b);
            }

            indices[(i * 3) + 0] = a;
            indices[(i * 3) + 1] = b;
            indices[(i * 3) + 2] = c;
        }

        return ([.. positions], indices);

        int Midpoint(int a, int b) {
            var key = a < b ? (a, b) : (b, a);

            if (midpoints.TryGetValue(key, out var existing)) {
                return existing;
            }

            positions.Add(Vector3.Normalize((positions[a] + positions[b]) * 0.5f));
            midpoints[key] = positions.Count - 1;

            return positions.Count - 1;
        }
    }
}
