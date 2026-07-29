// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields.Tests;

/// <summary>
///     The two meshes whose distance is known in closed form, and the closed forms themselves.
/// </summary>
/// <remarks>
///     Built here rather than taken from <c>Vixen.Rendering.MeshPrimitives</c> on purpose: these
///     tests exist to check a bake against arithmetic, and borrowing a primitive would make the
///     renderer's winding conventions a silent input to the answer. A box is exactly its closed form,
///     which makes it the strongest check available — any disagreement is the bake's.
/// </remarks>
static class Shapes {
    /// <summary>An axis-aligned box centred on the origin, wound so every face looks outward.</summary>
    /// <param name="half">Half the box's size along each axis.</param>
    /// <returns>Eight vertices and twelve triangles.</returns>
    public static (Vector3[] Vertices, int[] Indices) Box(Vector3 half) {
        Vector3[] vertices = [
            new(-half.X, -half.Y, -half.Z),
            new(half.X, -half.Y, -half.Z),
            new(half.X, half.Y, -half.Z),
            new(-half.X, half.Y, -half.Z),
            new(-half.X, -half.Y, half.Z),
            new(half.X, -half.Y, half.Z),
            new(half.X, half.Y, half.Z),
            new(-half.X, half.Y, half.Z)
        ];

        int[] indices = [
            4, 5, 6, 4, 6, 7, // +Z
            0, 3, 2, 0, 2, 1, // −Z
            1, 2, 6, 1, 6, 5, // +X
            0, 4, 7, 0, 7, 3, // −X
            3, 7, 6, 3, 6, 2, // +Y
            0, 1, 5, 0, 5, 4 // −Y
        ];

        return (vertices, indices);
    }

    /// <summary>The same box with its top face left off, which is what a mesh usually is.</summary>
    /// <param name="half">Half the box's size along each axis.</param>
    /// <returns>Eight vertices and ten triangles.</returns>
    /// <remarks>
    ///     The case a parity test inverts and a backface vote survives — see
    ///     <see cref="MeshDistanceFieldBaker" />.
    /// </remarks>
    public static (Vector3[] Vertices, int[] Indices) OpenBox(Vector3 half) {
        var (vertices, indices) = Box(half);

        // Drop the +Y face: the fifth of six, so indices 24 through 29.
        int[] open = [.. indices[..24], .. indices[30..]];

        return (vertices, open);
    }

    /// <summary>A UV sphere centred on the origin, wound so every face looks outward.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="rings">How many steps of latitude.</param>
    /// <param name="segments">How many steps of longitude.</param>
    /// <returns>The vertices and triangles.</returns>
    public static (Vector3[] Vertices, int[] Indices) Sphere(float radius, int rings, int segments) {
        var vertices = new Vector3[(rings + 1) * (segments + 1)];

        for (var ring = 0; ring <= rings; ring++) {
            var theta = MathF.PI * ring / rings;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (var segment = 0; segment <= segments; segment++) {
                var phi = 2f * MathF.PI * segment / segments;

                vertices[(ring * (segments + 1)) + segment] = new Vector3(
                    sinTheta * MathF.Cos(phi),
                    cosTheta,
                    sinTheta * MathF.Sin(phi)
                ) * radius;
            }
        }

        var indices = new List<int>(rings * segments * 6);

        for (var ring = 0; ring < rings; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var here = (ring * (segments + 1)) + segment;
                var below = here + segments + 1;

                indices.Add(here);
                indices.Add(here + 1);
                indices.Add(below);

                indices.Add(here + 1);
                indices.Add(below + 1);
                indices.Add(below);
            }
        }

        return (vertices, [.. indices]);
    }

    /// <summary>The exact signed distance from a point to an origin-centred sphere.</summary>
    /// <param name="point">The point.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>The distance, negative inside.</returns>
    public static float SphereDistance(Vector3 point, float radius) => point.Length() - radius;

    /// <summary>The exact signed distance from a point to an origin-centred box.</summary>
    /// <param name="point">The point.</param>
    /// <param name="half">Half the box's size along each axis.</param>
    /// <returns>The distance, negative inside.</returns>
    /// <remarks>
    ///     The two terms are disjoint: outside the box the second is zero and the first is the
    ///     distance to the nearest face, edge or corner; inside it the first is zero and the second
    ///     is the negative distance to the nearest face.
    /// </remarks>
    public static float BoxDistance(Vector3 point, Vector3 half) {
        var q = new Vector3(MathF.Abs(point.X), MathF.Abs(point.Y), MathF.Abs(point.Z)) - half;
        var outside = new Vector3(MathF.Max(q.X, 0), MathF.Max(q.Y, 0), MathF.Max(q.Z, 0)).Length();
        var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);

        return outside + inside;
    }
}
