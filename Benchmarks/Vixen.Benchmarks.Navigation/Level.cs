// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Benchmarks.Navigation;

/// <summary>
///     A square floor with a regular grid of pillars on it — enough geometry that the bake has
///     something to trace and the pathfinder something to walk round.
/// </summary>
/// <remarks>
///     Regular rather than random, so a run is comparable with the one before it. The pillar spacing
///     is what decides the polygon count, and it is deliberately tight enough that a path across the
///     level turns several times: a benchmark over an empty floor measures a single polygon and says
///     nothing about the search.
/// </remarks>
public static class Level {
    /// <summary>Builds the level.</summary>
    /// <param name="size">How wide the floor is, in metres.</param>
    /// <param name="spacing">How far apart the pillars are.</param>
    /// <returns>The vertices and the triangle indices.</returns>
    public static (Vector3[] Vertices, int[] Indices) Build(float size, float spacing = 8f) {
        var vertices = new List<Vector3>();
        var indices = new List<int>();

        Quad(vertices, indices, new(0, 0, 0), new(size, 0, size));

        for (var z = spacing; z < size - spacing; z += spacing) {
            for (var x = spacing; x < size - spacing; x += spacing) {
                Pillar(vertices, indices, new(x, 0, z), 1.5f, 3f);
            }
        }

        return ([.. vertices], [.. indices]);
    }

    static void Pillar(List<Vector3> vertices, List<int> indices, Vector3 centre, float width, float height) {
        var half = width * 0.5f;
        var minimum = new Vector3(centre.X - half, centre.Y, centre.Z - half);
        var maximum = new Vector3(centre.X + half, centre.Y + height, centre.Z + half);

        Quad(vertices, indices, new(minimum.X, maximum.Y, minimum.Z), new(maximum.X, maximum.Y, maximum.Z));
        Wall(vertices, indices, new(minimum.X, minimum.Y, minimum.Z), new(minimum.X, maximum.Y, maximum.Z));
        Wall(vertices, indices, new(maximum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, maximum.Z));
        Wall(vertices, indices, new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, minimum.Z));
        Wall(vertices, indices, new(minimum.X, minimum.Y, maximum.Z), new(maximum.X, maximum.Y, maximum.Z));
    }

    /// <summary>A horizontal quad, wound so its front face points up.</summary>
    static void Quad(List<Vector3> vertices, List<int> indices, Vector3 minimum, Vector3 maximum) {
        var first = vertices.Count;

        vertices.Add(new(minimum.X, maximum.Y, minimum.Z));
        vertices.Add(new(minimum.X, maximum.Y, maximum.Z));
        vertices.Add(new(maximum.X, maximum.Y, maximum.Z));
        vertices.Add(new(maximum.X, maximum.Y, minimum.Z));

        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }

    static void Wall(List<Vector3> vertices, List<int> indices, Vector3 from, Vector3 to) {
        var first = vertices.Count;

        vertices.Add(new(from.X, from.Y, from.Z));
        vertices.Add(new(to.X, from.Y, to.Z));
        vertices.Add(new(to.X, to.Y, to.Z));
        vertices.Add(new(from.X, to.Y, from.Z));

        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }
}
