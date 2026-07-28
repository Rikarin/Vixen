// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Tests;

/// <summary>
///     Level geometry, built by hand, because a navmesh test that could not say exactly what it was
///     given could not say what the answer should be.
/// </summary>
/// <remarks>
///     Everything here is wound so that the upward face is the front one — <c>Cross(b - a, c - a)</c>
///     points at +Y — because that is what decides whether the slope test calls a triangle ground.
/// </remarks>
internal sealed class NavTestGeometry {
    readonly List<Vector3> vertices = [];
    readonly List<int> indices = [];

    public ReadOnlySpan<Vector3> Vertices => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);

    public ReadOnlySpan<int> Indices => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices);

    /// <summary>A flat floor spanning a rectangle in XZ.</summary>
    public NavTestGeometry Floor(float minX, float minZ, float maxX, float maxZ, float y = 0f) {
        var first = vertices.Count;

        vertices.Add(new(minX, y, minZ));
        vertices.Add(new(minX, y, maxZ));
        vertices.Add(new(maxX, y, maxZ));
        vertices.Add(new(maxX, y, minZ));

        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);

        return this;
    }

    /// <summary>An axis-aligned box, as six quads. The four sides are what makes it an obstacle.</summary>
    public NavTestGeometry Box(Vector3 minimum, Vector3 maximum) {
        Floor(minimum.X, minimum.Z, maximum.X, maximum.Z, maximum.Y);
        Floor(minimum.X, minimum.Z, maximum.X, maximum.Z, minimum.Y);

        Wall(new(minimum.X, minimum.Y, minimum.Z), new(minimum.X, maximum.Y, maximum.Z));
        Wall(new(maximum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, maximum.Z));
        Wall(new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, minimum.Z));
        Wall(new(minimum.X, minimum.Y, maximum.Z), new(maximum.X, maximum.Y, maximum.Z));

        return this;
    }

    /// <summary>Ground that is not flat: a grid of quads whose height comes from a function.</summary>
    /// <remarks>
    ///     For the tests that are about how closely the navmesh follows the ground rather than about
    ///     where its edges are. A flat floor cannot tell a detail mesh from no detail mesh, because a
    ///     polygon over a flat floor is already exactly right.
    /// </remarks>
    public NavTestGeometry Terrain(float minX, float minZ, float maxX, float maxZ, int cells, Func<float, float, float> height) {
        for (var row = 0; row < cells; row++) {
            for (var column = 0; column < cells; column++) {
                var x0 = minX + ((maxX - minX) * column / cells);
                var x1 = minX + ((maxX - minX) * (column + 1) / cells);
                var z0 = minZ + ((maxZ - minZ) * row / cells);
                var z1 = minZ + ((maxZ - minZ) * (row + 1) / cells);

                var first = vertices.Count;

                vertices.Add(new(x0, height(x0, z0), z0));
                vertices.Add(new(x0, height(x0, z1), z1));
                vertices.Add(new(x1, height(x1, z1), z1));
                vertices.Add(new(x1, height(x1, z0), z0));

                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
        }

        return this;
    }

    /// <summary>A vertical quad. Which way it faces does not matter: nothing stands on a wall.</summary>
    public NavTestGeometry Wall(Vector3 from, Vector3 to) {
        var first = vertices.Count;

        vertices.Add(new(from.X, from.Y, from.Z));
        vertices.Add(new(to.X, from.Y, to.Z));
        vertices.Add(new(to.X, to.Y, to.Z));
        vertices.Add(new(from.X, to.Y, from.Z));

        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);

        return this;
    }
}
