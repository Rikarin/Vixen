// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>The port against the original, and against what a tangent has to be on a shape with an answer.</summary>
public sealed class MikkTSpaceTests {
    const float Close = 1e-5f;

    static Vector4[] Solve(MikkFixtures.Soup soup) => MikkTSpace.Generate(soup.Positions, soup.Normals, soup.TexCoords);

    /// <summary>What <c>mikktspace.c</c> printed for each fixture, compiled with no fused multiply-add.</summary>
    /// <remarks>
    ///     ⚠ <b>Near and not equal</b>, because the one call into a platform's maths library is the arc
    ///     cosine weighting a tangent, and a libm may round it differently from the one these came from.
    /// </remarks>
    static Dictionary<string, Vector4[]> Known() {
        using var stream = typeof(MikkTSpaceTests).Assembly.GetManifestResourceStream("MikkTSpaceKnownValues.txt");
        using var reader = new StreamReader(stream!);
        Dictionary<string, Vector4[]> known = [];

        while (reader.ReadLine() is { } header) {
            var parts = header.Split(' ');
            var values = new Vector4[int.Parse(parts[1], CultureInfo.InvariantCulture)];

            for (var at = 0; at < values.Length; at++) {
                var numbers = reader.ReadLine()!.Split(' ')
                    .Select(static text => float.Parse(text, CultureInfo.InvariantCulture))
                    .ToArray();

                values[at] = new(numbers[0], numbers[1], numbers[2], numbers[3]);
            }

            known.Add(parts[0], values);
        }

        return known;
    }

    public static TheoryData<string> Names() => [..MikkFixtures.All.Select(static soup => soup.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void The_port_gives_what_the_original_gives(string name) {
        var soup = MikkFixtures.All.Single(soup => soup.Name == name);
        var expected = Known()[name];
        var actual = Solve(soup);

        Assert.Equal(expected.Length, actual.Length);

        for (var at = 0; at < expected.Length; at++) {
            Assert.True(
                MathF.Abs(expected[at].X - actual[at].X) < Close
                && MathF.Abs(expected[at].Y - actual[at].Y) < Close
                && MathF.Abs(expected[at].Z - actual[at].Z) < Close
                && expected[at].W == actual[at].W,
                $"corner {at}: expected {expected[at]}, got {actual[at]}"
            );
        }
    }

    [Fact]
    public void A_plane_has_u_along_x_and_the_top_of_the_image_along_y() {
        var soup = MikkFixtures.Plane();

        Assert.All(
            Solve(soup),
            static tangent => {
                Assert.Equal(new(1f, 0f, 0f, 1f), tangent);
                Assert.Equal(Vector3.UnitY, Vector3.Cross(Vector3.UnitZ, Vector3.UnitX) * tangent.W);
            }
        );
    }

    [Fact]
    public void A_cylinder_has_the_tangent_of_its_circle_at_every_corner() {
        var soup = MikkFixtures.Cylinder(32, 4);
        var tangents = Solve(soup);

        for (var at = 0; at < tangents.Length; at++) {
            var normal = soup.Normals[at];

            // Going round in u is d/dθ of (cos θ, 0, sin θ), and the image's top is +Y. The chart is
            // the outside of the tube seen with u to the left, so the frame is left-handed.
            var expected = new Vector3(-normal.Z, 0f, normal.X);
            var tangent = new Vector3(tangents[at].X, tangents[at].Y, tangents[at].Z);

            Assert.True((tangent - expected).Length() < Close, $"corner {at}: {tangent}, expected {expected}");
            Assert.Equal(-1f, tangents[at].W);
        }
    }

    [Fact]
    public void A_mirrored_chart_changes_the_sign_and_not_the_surface() {
        var soup = MikkFixtures.Mirrored();
        var tangents = Solve(soup);

        for (var triangle = 0; triangle < tangents.Length / 3; triangle++) {
            var middle = soup.Positions[triangle * 3]
                + soup.Positions[triangle * 3 + 1]
                + soup.Positions[triangle * 3 + 2];

            // The far half of the tube is the one whose atlas runs back the other way.
            Assert.Equal(middle.Z < 0f ? 1f : -1f, tangents[triangle * 3].W);
        }
    }

    [Fact]
    public void A_hard_edge_keeps_a_frame_on_each_side() {
        var soup = MikkFixtures.HardBox();
        var tangents = Solve(soup);

        for (var at = 0; at < tangents.Length; at++) {
            var tangent = new Vector3(tangents[at].X, tangents[at].Y, tangents[at].Z);

            Assert.Equal(0f, Vector3.Dot(tangent, soup.Normals[at]), 5);
        }

        // One point, three faces. Each keeps its own face's tangent, which an average across the edge would not.
        var corner = new Vector3(1f, 1f, 1f);
        var seen = Enumerable.Range(0, tangents.Length)
            .Where(at => soup.Positions[at] == corner)
            .Select(at => (soup.Normals[at], tangents[at]))
            .Distinct()
            .ToDictionary(static pair => pair.Item1, static pair => pair.Item2);

        Assert.Equal(new(1f, 0f, 0f, 1f), seen[Vector3.UnitZ]);
        Assert.Equal(new(0f, 0f, -1f, 1f), seen[Vector3.UnitX]);
        Assert.Equal(new(1f, 0f, 0f, 1f), seen[Vector3.UnitY]);
    }

    [Fact]
    public void Neither_the_order_of_the_triangles_nor_their_first_corner_counts() {
        var soup = MikkFixtures.Cylinder(16, 4);
        var expected = Solve(soup);
        var random = new Random(3);
        var count = soup.Positions.Length / 3;
        var order = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToArray();
        var turns = order.Select(_ => random.Next(3)).ToArray();
        var from = new int[soup.Positions.Length];

        for (var slot = 0; slot < count; slot++) {
            for (var corner = 0; corner < 3; corner++) {
                from[slot * 3 + corner] = order[slot] * 3 + (corner + turns[slot]) % 3;
            }
        }

        var actual = MikkTSpace.Generate(
            [..from.Select(at => soup.Positions[at])],
            [..from.Select(at => soup.Normals[at])],
            [..from.Select(at => soup.TexCoords[at])]
        );

        for (var at = 0; at < from.Length; at++) {
            Assert.True((actual[at] - expected[from[at]]).Length() < Close, $"corner {at}");
        }
    }

    [Fact]
    public void A_triangle_with_no_area_takes_a_space_from_a_good_corner_it_is_welded_to() {
        var soup = MikkFixtures.Plane();
        var positions = soup.Positions.Concat([soup.Positions[0], soup.Positions[0], soup.Positions[2]]).ToArray();
        var normals = soup.Normals.Concat([soup.Normals[0], soup.Normals[0], soup.Normals[2]]).ToArray();
        var coordinates = soup.TexCoords.Concat([soup.TexCoords[0], soup.TexCoords[0], soup.TexCoords[2]]).ToArray();

        var tangents = MikkTSpace.Generate(positions, normals, coordinates);

        Assert.All(tangents, static tangent => Assert.Equal(new(1f, 0f, 0f, 1f), tangent));
    }

    [Fact]
    public void Nothing_in_gives_nothing_out() => Assert.Empty(MikkTSpace.Generate([], [], []));

    [Fact]
    public void Corners_that_do_not_make_triangles_are_refused() {
        Assert.Throws<ArgumentException>(static () => MikkTSpace.Generate(
                [Vector3.Zero],
                [Vector3.UnitX],
                [Vector2.Zero]
            )
        );
        Assert.Throws<ArgumentException>(static () => MikkTSpace.Generate(
                [Vector3.Zero, Vector3.Zero, Vector3.Zero],
                [Vector3.UnitX],
                [Vector2.Zero]
            )
        );
    }
}
