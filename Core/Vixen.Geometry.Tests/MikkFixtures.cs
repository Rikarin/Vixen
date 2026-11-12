// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Tests;

/// <summary>Triangle soups for the tangent tests, each a case the algorithm treats differently.</summary>
static class MikkFixtures {
    public sealed record Soup(string Name, Vector3[] Positions, Vector3[] Normals, Vector2[] TexCoords);

    /// <summary>Every fixture, in the order of the known values.</summary>
    /// <remarks>
    ///     ⚠ <b>Seeds 64 and 274 are chosen.</b> They are two of the 26 in 2,000 where sorting the last run of
    ///     edges, which the original leaves unsorted, changes a tangent. Without them a tidied sort stays green.
    /// </remarks>
    public static IReadOnlyList<Soup> All { get; } =
        [Plane(), Cylinder(16, 4), Mirrored(), HardBox(), Awkward(7), Awkward(64), Awkward(274)];

    /// <summary>One flat quad of two triangles, with <c>u</c> along +X and the image's top along +Y.</summary>
    public static Soup Plane() {
        Vector3[] points =
            [new(0f, 0f, 0f), new(2f, 0f, 0f), new(2f, 3f, 0f), new(0f, 0f, 0f), new(2f, 3f, 0f), new(0f, 3f, 0f)];
        Vector2[] uvs = [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 1f), new(1f, 0f), new(0f, 0f)];

        return new("plane", points, Same(Vector3.UnitZ, 6), uvs);
    }

    /// <summary>A smooth open cylinder about +Y, with <c>u</c> round it and a seam where it closes.</summary>
    public static Soup Cylinder(int around, int up) {
        List<Vector3> points = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];

        void Corner(int column, int row) {
            var angle = MathF.Tau * column / around;
            var normal = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));

            points.Add(normal + Vector3.UnitY * ((float)row / up));
            normals.Add(normal);
            uvs.Add(new((float)column / around, 1f - (float)row / up));
        }

        for (var column = 0; column < around; column++) {
            for (var row = 0; row < up; row++) {
                Corner(column, row);
                Corner(column, row + 1);
                Corner(column + 1, row);
                Corner(column + 1, row);
                Corner(column, row + 1);
                Corner(column + 1, row + 1);
            }
        }

        return new("cylinder", [..points], [..normals], [..uvs]);
    }

    /// <summary>The cylinder with its atlas folded at the middle, so one half is mirrored.</summary>
    public static Soup Mirrored() {
        var soup = Cylinder(16, 2);
        var uvs = soup.TexCoords.Select(static uv => new Vector2(uv.X <= 0.5f ? uv.X : 1f - uv.X, uv.Y)).ToArray();

        return soup with { Name = "mirrored", TexCoords = uvs };
    }

    /// <summary>A cube of six hard faces that share points and no normals, on one small repeated chart.</summary>
    public static Soup HardBox() {
        List<Vector3> points = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];

        void Face(Vector3 normal, Vector3 right, Vector3 up) {
            var (a, b, c, d) = (normal - right - up, normal + right - up, normal + right + up, normal - right + up);

            points.AddRange([a, b, c, a, c, d]);
            normals.AddRange(Same(normal, 6));
            uvs.AddRange([new(0f, 0.5f), new(0.5f, 0.5f), new(0.5f, 0f), new(0f, 0.5f), new(0.5f, 0f), new(0f, 0f)]);
        }

        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);

        return new("hard-box", [..points], [..normals], [..uvs]);
    }

    /// <summary>
    ///     A jittered, shuffled grid with everything the algorithm has a case for: triangles of no area, triangles
    ///     with no texture area, a fin of three triangles on one edge, a mirrored patch, and doubled triangles.
    /// </summary>
    public static Soup Awkward(int seed) {
        var random = new Random(seed);
        const int Side = 12;
        var heights = new float[(Side + 1) * (Side + 1)];

        for (var at = 0; at < heights.Length; at++) {
            heights[at] = random.NextSingle() * 0.3f;
        }

        Vector3 Point(int x, int y) => new(x + 0.01f * (x * y % 3), y, heights[y * (Side + 1) + x]);

        Vector3 Normal(int x, int y) {
            var (left, right) = (Point(Math.Max(x - 1, 0), y), Point(Math.Min(x + 1, Side), y));
            var (down, up) = (Point(x, Math.Max(y - 1, 0)), Point(x, Math.Min(y + 1, Side)));

            return Vector3.Normalize(Vector3.Cross(right - left, up - down));
        }

        Vector2 Uv(int x, int y) => new(x > 8 ? (16f - x) / Side : (float)x / Side, 1f - (float)y / Side);

        List<(Vector3, Vector3, Vector2)[]> triangles = [];

        (Vector3, Vector3, Vector2) Corner(int x, int y) => (Point(x, y), Normal(x, y), Uv(x, y));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                triangles.Add([Corner(x, y), Corner(x + 1, y), Corner(x + 1, y + 1)]);
                triangles.Add([Corner(x, y), Corner(x + 1, y + 1), Corner(x, y + 1)]);
            }
        }

        var (one, two) = (Corner(3, 3), Corner(4, 3));

        triangles.Add([one, two, (new(3.5f, 3f, 2f), Vector3.UnitY, new(0.3f, 0.5f))]);
        triangles.Add([two, one, (new(3.5f, 3f, -2f), Vector3.UnitY, new(0.3f, 0.6f))]);
        triangles.Add([one, one, Corner(5, 5)]);
        triangles.Add([Corner(6, 6), Corner(6, 6), Corner(6, 6)]);
        triangles.Add([Corner(1, 1), Corner(2, 1) with { Item3 = Uv(1, 1) }, Corner(2, 2) with { Item3 = Uv(1, 1) }]);
        triangles.Add([..triangles[10]]);

        for (var at = triangles.Count - 1; at > 0; at--) {
            var other = random.Next(at + 1);

            (triangles[at], triangles[other]) = (triangles[other], triangles[at]);
        }

        var corners = triangles.SelectMany(static triangle => triangle).ToArray();

        return new(
            $"awkward-{seed}",
            [..corners.Select(static corner => corner.Item1)],
            [..corners.Select(static corner => corner.Item2)],
            [..corners.Select(static corner => corner.Item3)]
        );
    }

    static Vector3[] Same(Vector3 value, int count) => [..Enumerable.Repeat(value, count)];
}
