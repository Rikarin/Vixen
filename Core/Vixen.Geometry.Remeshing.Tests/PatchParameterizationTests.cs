// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D8's per-patch parameterization, on a patch the blend cannot fill.</summary>
/// <remarks>
///     <para>
///         <b>The subject is a spherical rectangle</b> — <c>140°</c> of arc in both directions — because
///         that is the shape the transfinite blend is worst on and the shape § D8's "a Coons blend of
///         four curved boundary chains is not injective" is about. All four of its boundary chains
///         curve, and both pairs curve <i>against</i> each other.
///     </para>
///     <para>
///         ⚠ <b>The contrast is measured inside the tests rather than asserted about.</b>
///         <see cref="TheBlendLeavesTheSurfaceWhereTheParameterizationDoesNot" /> computes the Coons
///         interior the old code would have produced from the same rim and reports how far off the
///         sphere it lands, so the two constructions are compared on one patch rather than across two
///         builds.
///     </para>
/// </remarks>
public class PatchParameterizationTests {
    /// <summary>How many quads the test patch is across and up.</summary>
    const int Across = 6;

    /// <summary>The polar angles the patch spans, in radians.</summary>
    const float FromPolar = 20f * MathF.PI / 180f;

    /// <summary>The far polar angle.</summary>
    const float ToPolar = 160f * MathF.PI / 180f;

    /// <summary>The azimuth the patch spans, in radians, either side of zero.</summary>
    const float Azimuth = 70f * MathF.PI / 180f;

    /// <summary>Every point the parameterization places is a point of the source surface.</summary>
    /// <remarks>
    ///     ⚠ <b>Because it is a barycentric point of one of the patch's own triangles and never a
    ///     blend that is projected back afterwards.</b> The bound is the triangulation's own chord
    ///     sagitta — a unit sphere cut into <see cref="Across" /> steps of 23° has its chords
    ///     <c>1 − cos(11.7°)</c> inside it — so anything further out or further in is the
    ///     parameterization missing, not the fixture being coarse.
    /// </remarks>
    [Fact]
    public void TheInteriorLandsOnTheSourceSurface() {
        var interior = Fill(out _, out _);

        Assert.NotNull(interior);

        for (var i = 0; i < interior.GetLength(0); i++) {
            for (var j = 0; j < interior.GetLength(1); j++) {
                var radius = interior[i, j].Length();

                Assert.InRange(radius, 0.97f, 1.0001f);
            }
        }
    }

    /// <summary>No quad of the filled grid is folded over itself.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property the whole file exists for, and the one <c>IsAllQuad</c> cannot see.</b>
    ///         A negative scaled Jacobian at any corner is a quad turned back through itself;
    ///         <see cref="QuadQualityTests" /> records that today's pipeline still has them and this
    ///         records that they do not come from a patch the parameterization filled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Tutte's theorem is why this can be an assertion rather than a hope.</b> The patch
    ///         is mapped onto the unit square with its rim pinned to the square's rim and every
    ///         interior vertex at a positive convex combination of its neighbours, so the map is an
    ///         embedding; a rectangle of the square therefore lifts to a cell that overlaps no other.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NoQuadOfTheFilledGridIsInverted() {
        var interior = Fill(out var rim, out var grid);

        Assert.NotNull(interior);

        var placed = Placed(interior, rim, grid);

        for (var i = 0; i < Across; i++) {
            for (var j = 0; j < Across; j++) {
                var loop = new[] { placed[i, j], placed[i + 1, j], placed[i + 1, j + 1], placed[i, j + 1] };
                var worst = ScaledJacobian(loop);

                Assert.True(
                    worst > 0f,
                    $"the quad at ({i}, {j}) scores {worst:F3}. A parameterized patch is the image of an "
                    + "embedding, so a corner at or below zero means the lift did not respect it."
                );
            }
        }
    }

    /// <summary>The blend the old code used leaves the sphere; the parameterization stays on it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the comparison against what was there before, made on one patch rather than
    ///     across two builds.</b> The Coons interior is the textbook one — the two linear
    ///     interpolations between opposite sides minus the bilinear interpolation of the corners the
    ///     two both counted — computed here from the same rim the parameterization was given. On this
    ///     patch its centre lands at roughly <c>0.57</c> of the radius: 43% of the way to the middle
    ///     of the sphere, which is the displacement <c>SurfaceProjector.Project</c> then has to undo
    ///     and the reason the interior arrived in no particular order.
    /// </remarks>
    [Fact]
    public void TheBlendLeavesTheSurfaceWhereTheParameterizationDoesNot() {
        var interior = Fill(out var rim, out var grid);

        Assert.NotNull(interior);

        var strayed = 0f;

        for (var i = 1; i < Across; i++) {
            for (var j = 1; j < Across; j++) {
                var u = (float) i / Across;
                var v = (float) j / Across;

                var along = (rim[grid[0][j]] * (1f - u)) + (rim[grid[Across][j]] * u);
                var across = (rim[grid[i][0]] * (1f - v)) + (rim[grid[i][Across]] * v);

                var corners = (rim[grid[0][0]] * (1f - u) * (1f - v))
                    + (rim[grid[Across][0]] * u * (1f - v))
                    + (rim[grid[0][Across]] * (1f - u) * v)
                    + (rim[grid[Across][Across]] * u * v);

                strayed = MathF.Max(strayed, MathF.Abs(1f - (along + across - corners).Length()));

                Assert.InRange(interior[i - 1, j - 1].Length(), 0.97f, 1.0001f);
            }
        }

        Assert.True(
            strayed > 0.25f,
            $"the blend's worst interior point is only {strayed:F3} off the unit sphere. The fixture is "
            + "supposed to be a patch the blend cannot fill, and if it can then this file is not "
            + "measuring the difference it says it is."
        );
    }

    /// <summary>A rim the map does not reproduce is refused, rather than filled against the wrong one.</summary>
    /// <remarks>
    ///     ⚠ <b>The guard that was missing, and it was worth 177° of bow-tie.</b> The interior is
    ///     solved against a rim pinned from the arcs and the grid's rim is the samples the extraction
    ///     already placed; where the two are not the same curve — an arc whose ends the extractor's
    ///     union-find merged elsewhere, a side that walks a vertex another side also walks — the
    ///     interior is correct about a boundary the output has never had, and the first ring of quads
    ///     reaches across the real one. Moving one rim sample a fifth of the patch off the surface is
    ///     that disagreement, made deliberately.
    /// </remarks>
    [Fact]
    public void ARimTheMapDoesNotReproduceIsRefused() {
        var mesh = Dome(out var corner);
        var patch = Patch(corner, out var arcs);
        var output = new EditMesh();
        var grid = Rim(mesh, corner, output, out var samples);

        Assert.NotNull(
            PatchParameterization.Interior(mesh, arcs, patch, samples, output, grid, Across, Across, out _)
        );

        output.MovePosition(grid[1][0], output.Positions[grid[1][0]] * 1.2f);

        Assert.Null(
            PatchParameterization.Interior(mesh, arcs, patch, samples, output, grid, Across, Across, out _)
        );
    }

    /// <summary>Builds the fixture and fills it.</summary>
    static Vector3[,]? Fill(out Vector3[] rim, out int[][] grid) {
        var mesh = Dome(out var corner);
        var patch = Patch(corner, out var arcs);
        var output = new EditMesh();

        grid = Rim(mesh, corner, output, out var samples);

        var found = PatchParameterization.Interior(mesh, arcs, patch, samples, output, grid, Across, Across, out _);

        rim = [.. output.Positions];

        return found;
    }

    /// <summary>A spherical rectangle, triangulated, with a lookup from its <c>(i, j)</c> corner.</summary>
    static ManifoldMesh Dome(out int[][] corner) {
        var positions = new List<Vector3>();
        var found = new int[Across + 1][];

        for (var i = 0; i <= Across; i++) {
            found[i] = new int[Across + 1];

            for (var j = 0; j <= Across; j++) {
                var polar = FromPolar + ((ToPolar - FromPolar) * i / Across);
                var azimuth = -Azimuth + (2f * Azimuth * j / Across);

                found[i][j] = positions.Count;

                positions.Add(
                    new(
                        MathF.Sin(polar) * MathF.Cos(azimuth),
                        MathF.Sin(polar) * MathF.Sin(azimuth),
                        MathF.Cos(polar)
                    )
                );
            }
        }

        var triangles = new List<int>();

        for (var i = 0; i < Across; i++) {
            for (var j = 0; j < Across; j++) {
                triangles.AddRange([found[i][j], found[i + 1][j], found[i + 1][j + 1]]);
                triangles.AddRange([found[i][j], found[i + 1][j + 1], found[i][j + 1]]);
            }
        }

        corner = found;

        return ManifoldMesh.Build([.. positions], [.. triangles], []);
    }

    /// <summary>The four sides, anticlockwise, each one arc.</summary>
    static LayoutPatch Patch(int[][] corner, out LayoutArc[] arcs) {
        int[] bottom = [.. Enumerable.Range(0, Across + 1).Select(i => corner[i][0])];
        int[] right = [.. Enumerable.Range(0, Across + 1).Select(j => corner[Across][j])];
        int[] top = [.. Enumerable.Range(0, Across + 1).Select(i => corner[i][Across])];
        int[] left = [.. Enumerable.Range(0, Across + 1).Select(j => corner[0][j])];

        arcs = [Arc(bottom), Arc(right), Arc(top), Arc(left)];

        return new() {
            Triangles = [.. Enumerable.Range(0, Across * Across * 2)],
            Sides = [
                [new(0, false)],
                [new(1, false)],
                [new(2, true)],
                [new(3, true)]
            ]
        };
    }

    /// <summary>One arc over a chain.</summary>
    static LayoutArc Arc(int[] chain) => new() { Vertices = chain, IsFeature = false, Length = 1f, Target = Across };

    /// <summary>
    ///     The grid's four sides, as output positions. ⚠ One sample per chain vertex, which is exact
    ///     here because equal steps of angle are equal chords on a sphere — so nothing about the
    ///     sampling can be the thing under test.
    /// </summary>
    static int[][] Rim(ManifoldMesh mesh, int[][] corner, EditMesh output, out int[][] samples) {
        var placed = new Dictionary<int, int>();

        int Place(int vertex) {
            if (!placed.TryGetValue(vertex, out var index)) {
                index = output.AddPosition(mesh.Positions[vertex]);
                placed[vertex] = index;
            }

            return index;
        }

        var grid = new int[Across + 1][];

        for (var i = 0; i <= Across; i++) {
            grid[i] = new int[Across + 1];
        }

        for (var i = 0; i <= Across; i++) {
            grid[i][0] = Place(corner[i][0]);
            grid[i][Across] = Place(corner[i][Across]);
        }

        for (var j = 0; j <= Across; j++) {
            grid[Across][j] = Place(corner[Across][j]);
            grid[0][j] = Place(corner[0][j]);
        }

        samples = [
            [.. Enumerable.Range(0, Across + 1).Select(i => grid[i][0])],
            [.. Enumerable.Range(0, Across + 1).Select(j => grid[Across][j])],
            [.. Enumerable.Range(0, Across + 1).Select(i => grid[i][Across])],
            [.. Enumerable.Range(0, Across + 1).Select(j => grid[0][j])]
        ];

        return grid;
    }

    /// <summary>Every grid position, rim and interior together.</summary>
    static Vector3[,] Placed(Vector3[,] interior, Vector3[] rim, int[][] grid) {
        var placed = new Vector3[Across + 1, Across + 1];

        for (var i = 0; i <= Across; i++) {
            for (var j = 0; j <= Across; j++) {
                placed[i, j] = i > 0 && i < Across && j > 0 && j < Across
                    ? interior[i - 1, j - 1]
                    : rim[grid[i][j]];
            }
        }

        return placed;
    }

    /// <summary>The worst corner of one quad, the same way <see cref="RemeshMetrics" /> measures it.</summary>
    static float ScaledJacobian(Vector3[] loop) {
        var normal = Vector3.Zero;

        for (var at = 0; at < loop.Length; at++) {
            var here = loop[at];
            var next = loop[(at + 1) % loop.Length];

            normal += new Vector3(
                (here.Y - next.Y) * (here.Z + next.Z),
                (here.Z - next.Z) * (here.X + next.X),
                (here.X - next.X) * (here.Y + next.Y)
            );
        }

        normal = ScaleSafe.Unit(normal);

        var worst = 1f;

        for (var at = 0; at < loop.Length; at++) {
            var here = loop[at];
            var ahead = ScaleSafe.Unit(loop[(at + 1) % loop.Length] - here);
            var behind = ScaleSafe.Unit(loop[(at + loop.Length - 1) % loop.Length] - here);

            worst = MathF.Min(worst, Vector3.Dot(Vector3.Cross(ahead, behind), normal));
        }

        return worst;
    }
}
