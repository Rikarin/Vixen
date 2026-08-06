// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The surfaces stage two and stage three are argued over, and the one measurement they share.</summary>
/// <remarks>
///     <para>
///         <b>Generated in code, like <see cref="BrokenMeshes" />, and for the same reasons.</b> No
///         test in this project reads a file — docs/plan/41 § D19 and <c>BrokenMeshes</c>' own remarks
///         settle that — so a genus-two surface has to be something a routine can build rather than
///         something somebody exported.
///     </para>
///     <para>
///         ⚠ <b>The genus-two case is a plate with two holes punched through it, and not a boolean.</b>
///         Two overlapping tori unioned through <see cref="MeshBoolean" /> came out open rather than
///         closed, and a box with two cylinders subtracted came out closed with an Euler characteristic
///         of −3 — which is impossible for a closed orientable surface and means the result still
///         carried degeneracies. A slab of cells with cells removed is a handle per hole by
///         construction, is exact, and is fifty lines. <see cref="Euler" /> checks the claim rather
///         than trusting it.
///     </para>
/// </remarks>
static class FieldFixtures {
    /// <summary>V − E + F, over the conditioned view.</summary>
    /// <param name="mesh">The surface.</param>
    /// <returns>The Euler characteristic — <c>2</c> for a sphere, <c>0</c> for a torus, <c>2 − 2g</c> in general.</returns>
    /// <remarks>
    ///     Vertices are counted by what the triangles actually reference, so a position left behind by
    ///     a de-speck does not add one; edges are counted as unordered pairs, so both halves of a
    ///     manifold edge are one.
    /// </remarks>
    public static int Euler(ManifoldMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var edges = new HashSet<long>();
        var vertices = new HashSet<int>();

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var a = mesh.Triangles[half];
            var b = mesh.Triangles[ManifoldMesh.Next(half)];

            vertices.Add(a);
            edges.Add(((long) Math.Min(a, b) << 32) | (uint) Math.Max(a, b));
        }

        return vertices.Count - edges.Count + mesh.TriangleCount;
    }

    /// <summary>A conditioned view of a source mesh.</summary>
    /// <param name="source">The mesh.</param>
    /// <param name="rounds">How many pre-remesh rounds.</param>
    /// <param name="target">The pre-remesh's target edge length, or zero for the mesh's own mean.</param>
    /// <returns>The view.</returns>
    public static ManifoldMesh Condition(EditMesh source, int rounds = 0, float target = 0f) =>
        MeshConditioner.Condition(source, new() { PreRemeshIterations = rounds }, out _, target);

    /// <summary>A slab of unit cells with some of them punched out — one handle per hole.</summary>
    /// <param name="wide">Cells across.</param>
    /// <param name="deep">Cells back.</param>
    /// <param name="holes">Which cells are missing. Must be interior and must not touch each other.</param>
    /// <returns>A closed manifold of quads.</returns>
    public static EditMesh Plate(int wide, int deep, (int X, int Z)[] holes) {
        ArgumentNullException.ThrowIfNull(holes);

        var mesh = new EditMesh();
        var index = new Dictionary<(int X, int Z, int Y), int>();

        int At(int x, int z, int y) {
            if (index.TryGetValue((x, z, y), out var found)) {
                return found;
            }

            found = mesh.AddPosition(new(x, y, z));
            index[(x, z, y)] = found;

            return found;
        }

        bool Solid(int x, int z) => x >= 0 && z >= 0 && x < wide && z < deep && !holes.Contains((x, z));

        for (var x = 0; x < wide; x++) {
            for (var z = 0; z < deep; z++) {
                if (!Solid(x, z)) {
                    continue;
                }

                mesh.AddFace([At(x, z, 1), At(x + 1, z, 1), At(x + 1, z + 1, 1), At(x, z + 1, 1)]);
                mesh.AddFace([At(x, z, 0), At(x, z + 1, 0), At(x + 1, z + 1, 0), At(x + 1, z, 0)]);

                if (!Solid(x - 1, z)) {
                    mesh.AddFace([At(x, z, 0), At(x, z, 1), At(x, z + 1, 1), At(x, z + 1, 0)]);
                }

                if (!Solid(x + 1, z)) {
                    mesh.AddFace([At(x + 1, z, 0), At(x + 1, z + 1, 0), At(x + 1, z + 1, 1), At(x + 1, z, 1)]);
                }

                if (!Solid(x, z - 1)) {
                    mesh.AddFace([At(x, z, 0), At(x + 1, z, 0), At(x + 1, z, 1), At(x, z, 1)]);
                }

                if (!Solid(x, z + 1)) {
                    mesh.AddFace([At(x, z + 1, 0), At(x, z + 1, 1), At(x + 1, z + 1, 1), At(x + 1, z + 1, 0)]);
                }
            }
        }

        return mesh;
    }

    /// <summary>Refines a blocky mesh and then rounds it — an umbrella pass with no reprojection.</summary>
    /// <param name="source">The mesh.</param>
    /// <param name="target">The pre-remesh's target edge length.</param>
    /// <param name="rounds">How many smoothing rounds.</param>
    /// <returns>A smooth surface with the source's topology.</returns>
    /// <remarks>
    ///     ⚠ <b>Deliberately <i>not</i> the pre-remesh's relaxation, which reprojects.</b> R1's
    ///     relaxation puts every vertex back on the source surface each round, so a box stays a box
    ///     with sharp edges however many rounds it runs — which is right for conditioning and useless
    ///     here. What the index test needs is a surface with no ninety-degree turn on it, because a
    ///     cross field's period jump is only defined while the field turns by less than forty-five
    ///     degrees across an edge, and a sharp crease guarantees one that does not.
    /// </remarks>
    public static ManifoldMesh Smooth(EditMesh source, float target, int rounds) {
        var mesh = Condition(source, 4, target);
        var positions = mesh.Positions.ToArray();
        var next = new Vector3[positions.Length];

        for (var round = 0; round < rounds; round++) {
            for (var vertex = 0; vertex < positions.Length; vertex++) {
                var ring = mesh.Ring(vertex);

                if (ring.Length == 0) {
                    next[vertex] = positions[vertex];

                    continue;
                }

                var sum = Vector3.Zero;

                foreach (var neighbour in ring) {
                    sum += positions[neighbour];
                }

                next[vertex] = Vector3.Lerp(positions[vertex], sum / ring.Length, 0.6f);
            }

            (positions, next) = (next, positions);
        }

        return ManifoldMesh.Build(positions, mesh.Triangles.ToArray(), []);
    }

    /// <summary>An open tube: rings of quads round a cylinder, with no caps.</summary>
    /// <param name="sides">Vertices round it.</param>
    /// <param name="rings">Segments along it.</param>
    /// <param name="radius">How wide.</param>
    /// <param name="length">How long.</param>
    /// <returns>The view. Its two rims are boundaries, which is what the rim rules are tested on.</returns>
    public static ManifoldMesh Tube(int sides, int rings, float radius, float length) {
        var points = new Vector3[sides * (rings + 1)];

        for (var ring = 0; ring <= rings; ring++) {
            for (var side = 0; side < sides; side++) {
                var angle = MathF.Tau * side / sides;

                points[(ring * sides) + side] = new(
                    MathF.Cos(angle) * radius,
                    length * ring / rings,
                    MathF.Sin(angle) * radius
                );
            }
        }

        var indices = new List<int>();

        for (var ring = 0; ring < rings; ring++) {
            for (var side = 0; side < sides; side++) {
                var a = (ring * sides) + side;
                var b = (ring * sides) + ((side + 1) % sides);

                indices.AddRange([a, b, a + sides]);
                indices.AddRange([b, b + sides, a + sides]);
            }
        }

        return ManifoldMesh.Build(points, [.. indices], []);
    }

    /// <summary>The same surface at a different size, with exactly the same topology.</summary>
    /// <param name="mesh">The view.</param>
    /// <param name="factor">What to multiply every position by.</param>
    /// <returns>The scaled view.</returns>
    /// <remarks>
    ///     ⚠ <b>Scaled after conditioning rather than before, and R1 recorded why.</b>
    ///     <c>ScaleInvarianceTests</c> measured that five rounds of the pre-remesh do <i>not</i> agree
    ///     exactly at a thousandth and a thousand times — <c>0.001f</c> is not a binary fraction, so
    ///     each coordinate is perturbed by an ulp and a staircase mesh full of exactly-equal edge
    ///     lengths breaks its ties differently. Scaling a finished view keeps that out of the
    ///     comparison, so what is left is a statement about stage two.
    /// </remarks>
    public static ManifoldMesh Scaled(ManifoldMesh mesh, float factor) {
        ArgumentNullException.ThrowIfNull(mesh);

        var positions = new Vector3[mesh.VertexCount];

        for (var vertex = 0; vertex < positions.Length; vertex++) {
            positions[vertex] = mesh.Positions[vertex] * factor;
        }

        var groups = new int[mesh.TriangleCount];

        for (var triangle = 0; triangle < groups.Length; triangle++) {
            groups[triangle] = mesh.Group(triangle);
        }

        return ManifoldMesh.Build(positions, mesh.Triangles.ToArray(), groups);
    }

    /// <summary>How near two fields are, as the largest 4-RoSy angle between them.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="one">The first field.</param>
    /// <param name="two">The second.</param>
    /// <returns>The largest angle, in radians.</returns>
    public static float Apart(ManifoldMesh mesh, CrossField one, CrossField two) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(two);

        var worst = 0f;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            var here = one.Direction(vertex);
            var there = two.Direction(vertex);

            if (here.LengthSquared() <= 0f || there.LengthSquared() <= 0f) {
                continue;
            }

            var best = CrossField.Align(there, here, mesh.VertexNormal(vertex));

            worst = MathF.Max(worst, MathF.Acos(Math.Clamp(Vector3.Dot(best, here), -1f, 1f)));
        }

        return worst;
    }
}
