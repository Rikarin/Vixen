// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The tetrahedralisation, checked against its own definition rather than against a picture.
/// </summary>
/// <remarks>
///     <para>
///         "Delaunay" is not a look, it is four properties, and <see cref="AssertIsDelaunay" />
///         asserts all four on every mesh every test builds: the cells are positively oriented,
///         adjacency is symmetric and agrees about which triangle it is across, no vertex is
///         inside any cell's circumsphere, and the whole thing fills the convex hull of the
///         input. The first attempt at this construction failed the last two, and looked fine.
///     </para>
///     <para>
///         The inputs are the ones that break naive builders: a grid, whose cells are cospherical
///         eight points at a time; a slab, whose cells are nearly flat and have circumspheres far
///         larger than the point set; and a plane, which has no tetrahedralisation at all and must
///         say so instead of producing one.
///     </para>
/// </remarks>
public class DelaunayTetrahedralizationTests {
    static readonly Gen<Vector3> Points =
        Gen.Select(
            Gen.Float[-10f, 10f],
            Gen.Float[-10f, 10f],
            Gen.Float[-10f, 10f],
            static (x, y, z) => new Vector3(x, y, z)
        );

    // --- The small cases ----------------------------------------------------

    /// <summary>Four points are one cell.</summary>
    /// <remarks>
    ///     The case the first attempt got wrong in the most visible way: an enclosing tetrahedron
    ///     sized so that every circumsphere swallowed the domain produced no cells at all from
    ///     four points. There is exactly one answer here and it is not zero.
    /// </remarks>
    [Fact]
    public void Four_points_make_one_cell() {
        var mesh = DelaunayTetrahedralization.Build(
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f)]
        );

        Assert.Equal(1, mesh.CellCount);
        Assert.False(mesh.IsDegenerate);
        AssertIsDelaunay(mesh);

        // Its four faces are the hull, so none of them has a neighbour.
        foreach (var neighbour in mesh.CellNeighbours) {
            Assert.Equal(-1, neighbour);
        }
    }

    /// <summary>Fewer than four distinct points has no answer, and says so.</summary>
    [Fact]
    public void Three_points_are_degenerate() {
        var mesh = DelaunayTetrahedralization.Build([new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)]);

        Assert.True(mesh.IsDegenerate);
        Assert.Equal(0, mesh.CellCount);
        Assert.False(mesh.TryFind(new(0.1f, 0.1f, 0f), out _, out _));
    }

    /// <summary>Repeated positions are one vertex, not several.</summary>
    /// <remarks>
    ///     Two probes at the same place is a thing an author produces by duplicating one and
    ///     forgetting to move it. Two coincident vertices have no orientation between them, so
    ///     merging is not a convenience — it is the only thing that leaves the input in a state
    ///     the predicates can answer about.
    /// </remarks>
    [Fact]
    public void Coincident_positions_are_merged() {
        var mesh = DelaunayTetrahedralization.Build([
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(0f, 0f, 0f),
            new(0f, 1f, 0f),
            new(1f, 0f, 0f),
            new(0f, 0f, 1f)
        ]);

        Assert.Equal(4, mesh.Vertices.Length);
        Assert.Equal(1, mesh.CellCount);
    }

    /// <summary>A whole floor of probes at one height has no volume, and is reported as having none.</summary>
    /// <remarks>
    ///     Not an error. A single-layer grid is a legitimate thing to author, and the honest answer
    ///     is that there is nothing to interpolate tetrahedrally over — which lets the caller fall
    ///     back rather than leaving it to discover the problem in a frame.
    /// </remarks>
    [Fact]
    public void Coplanar_points_are_degenerate() {
        var points = new List<Vector3>();

        for (var x = 0; x < 4; x++) {
            for (var z = 0; z < 4; z++) {
                points.Add(new(x, 2f, z));
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());

        Assert.True(mesh.IsDegenerate);
        Assert.False(mesh.FillsConvexHull);
    }

    // --- The inputs that break naive builders -------------------------------

    /// <summary>Eight cospherical points still make a mesh that fills the cube they bound.</summary>
    [Fact]
    public void A_cubes_corners_tetrahedralise() {
        var mesh = DelaunayTetrahedralization.Build([
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(0f, 1f, 0f),
            new(1f, 1f, 0f),
            new(0f, 0f, 1f),
            new(1f, 0f, 1f),
            new(0f, 1f, 1f),
            new(1f, 1f, 1f)
        ]);

        AssertIsDelaunay(mesh);
        Assert.Equal(1d, TotalVolume(mesh), 5);
    }

    /// <summary>A grid — the layout light probes are actually authored in.</summary>
    /// <remarks>
    ///     Every eight neighbouring probes are cospherical, so the in-sphere test returns zero
    ///     several times per insertion and the whole mesh is built out of tie-breaks. The volume
    ///     is what says the tie-breaks were consistent: an inconsistent one leaves a hole or an
    ///     overlap, and either shows up as a total that is not the box.
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_grid_tetrahedralises_and_fills_its_box(int side) {
        var points = new List<Vector3>();

        for (var x = 0; x < side; x++) {
            for (var y = 0; y < side; y++) {
                for (var z = 0; z < side; z++) {
                    points.Add(new(x, y, z));
                }
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());

        Assert.Equal(points.Count, mesh.Vertices.Length);
        AssertIsDelaunay(mesh);

        var extent = side - 1d;
        Assert.Equal(extent * extent * extent, TotalVolume(mesh), 4);
    }

    /// <summary>A slab — probes spread wide and stacked thin, which is most rooms.</summary>
    /// <remarks>
    ///     The layout whose cells are nearly flat, and whose circumspheres are therefore hundreds
    ///     of times larger than the points that made them. That is what the enclosure has to be
    ///     bigger than, and it is why the enclosure is sized in millions rather than in tens.
    /// </remarks>
    [Fact]
    public void A_thin_slab_tetrahedralises() {
        var points = new List<Vector3>();

        for (var x = 0; x < 5; x++) {
            for (var z = 0; z < 5; z++) {
                points.Add(new(x * 4f, 0f, z * 4f));
                points.Add(new(x * 4f, 0.01f, z * 4f));
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());

        AssertIsDelaunay(mesh);
        Assert.Equal(16d * 16d * 0.01d, TotalVolume(mesh), 4);
    }

    /// <summary>Points on a sphere: every cell is cospherical with the whole input.</summary>
    /// <remarks>
    ///     The worst case for the tie-break, since there is no non-degenerate configuration
    ///     anywhere in the set to fall back on. It is also the one where a lenient in-sphere test
    ///     deletes the entire mesh on every insertion.
    /// </remarks>
    [Fact]
    public void Points_on_a_sphere_tetrahedralise() {
        var points = new List<Vector3> { Vector3.Zero };

        // A lattice of points at distance 3 from the origin — 3² = 1² + 2² + 2², so every
        // permutation and sign of (1, 2, 2) is exactly cospherical with every other.
        foreach (var permutation in (int[][])[[1, 2, 2], [2, 1, 2], [2, 2, 1]]) {
            for (var signs = 0; signs < 8; signs++) {
                points.Add(
                    new(
                        (signs & 1) == 0 ? permutation[0] : -permutation[0],
                        (signs & 2) == 0 ? permutation[1] : -permutation[1],
                        (signs & 4) == 0 ? permutation[2] : -permutation[2]
                    )
                );
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());

        Assert.Equal(25, mesh.Vertices.Length);
        AssertIsDelaunay(mesh);
    }

    /// <summary>Arbitrary clouds, since the interesting failures are the ones nobody writes down.</summary>
    [Fact]
    public void Generated_point_clouds_tetrahedralise() {
        Points.Array[8, 30]
            .Sample(
                static points => {
                    var mesh = DelaunayTetrahedralization.Build(points);

                    if (mesh.IsDegenerate) {
                        return;
                    }

                    AssertIsDelaunay(mesh);
                },
                iter: 200
            );
    }

    // --- Lookup -------------------------------------------------------------

    /// <summary>The weights a lookup returns rebuild the position they were asked about.</summary>
    /// <remarks>
    ///     Which is the whole contract: a probe blend is a weighted sum, and weights that do not
    ///     reproduce the position are a blend of the wrong four probes in the wrong proportions.
    /// </remarks>
    [Fact]
    public void A_found_cells_weights_reconstruct_the_position() {
        var points = new List<Vector3>();

        for (var x = 0; x < 4; x++) {
            for (var y = 0; y < 4; y++) {
                for (var z = 0; z < 4; z++) {
                    points.Add(new(x, y, z));
                }
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());

        Gen.Select(Gen.Float[0f, 3f], Gen.Float[0f, 3f], Gen.Float[0f, 3f])
            .Sample(
                query => {
                    var position = new Vector3(query.Item1, query.Item2, query.Item3);

                    Assert.True(mesh.TryFind(position, out var cell, out var weights));

                    var rebuilt = (mesh.Vertices[mesh.CellVertices[cell * 4]] * weights.X)
                        + (mesh.Vertices[mesh.CellVertices[(cell * 4) + 1]] * weights.Y)
                        + (mesh.Vertices[mesh.CellVertices[(cell * 4) + 2]] * weights.Z)
                        + (mesh.Vertices[mesh.CellVertices[(cell * 4) + 3]] * weights.W);

                    Assert.True(
                        Vector3.Distance(position, rebuilt) < 1e-4f,
                        $"{position} came back as {rebuilt}"
                    );

                    Assert.True(weights.X >= -1e-5f && weights.Y >= -1e-5f);
                    Assert.True(weights.Z >= -1e-5f && weights.W >= -1e-5f);
                    Assert.Equal(1f, weights.X + weights.Y + weights.Z + weights.W, 4);
                }
            );
    }

    /// <summary>Outside the hull is a miss, not a nearest guess.</summary>
    [Fact]
    public void Positions_outside_the_hull_are_not_found() {
        var mesh = DelaunayTetrahedralization.Build(
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f)]
        );

        Assert.False(mesh.TryFind(new(-1f, -1f, -1f), out _, out _));
        Assert.False(mesh.TryFind(new(1f, 1f, 1f), out _, out _));
        Assert.True(mesh.TryFind(new(0.1f, 0.1f, 0.1f), out _, out _));
    }

    /// <summary>A stale hint costs a longer walk and nothing else.</summary>
    [Fact]
    public void A_hint_does_not_change_the_answer() {
        var points = new List<Vector3>();

        for (var x = 0; x < 4; x++) {
            for (var y = 0; y < 4; y++) {
                for (var z = 0; z < 4; z++) {
                    points.Add(new(x, y, z));
                }
            }
        }

        var mesh = DelaunayTetrahedralization.Build(points.ToArray());
        var position = new Vector3(1.7f, 0.3f, 2.4f);

        Assert.True(mesh.TryFind(position, out var expected, out _));

        for (var hint = -5; hint < mesh.CellCount + 5; hint += 3) {
            Assert.True(mesh.TryFind(position, hint, out var cell, out _));
            Assert.Equal(expected, cell);
        }
    }

    // --- The definition -----------------------------------------------------

    /// <summary>All four properties that make a mesh the Delaunay tetrahedralisation.</summary>
    static void AssertIsDelaunay(DelaunayTetrahedralization mesh) {
        Assert.True(mesh.CellCount > 0);
        Assert.True(mesh.FillsConvexHull, "the cells do not fill the convex hull of the input");

        for (var cell = 0; cell < mesh.CellCount; cell++) {
            var a = mesh.Vertices[mesh.CellVertices[cell * 4]];
            var b = mesh.Vertices[mesh.CellVertices[(cell * 4) + 1]];
            var c = mesh.Vertices[mesh.CellVertices[(cell * 4) + 2]];
            var d = mesh.Vertices[mesh.CellVertices[(cell * 4) + 3]];

            Assert.Equal(1, ExactPredicates.Orient3D(a, b, c, d));

            for (var vertex = 0; vertex < mesh.Vertices.Length; vertex++) {
                if (IsCorner(mesh, cell, vertex)) {
                    continue;
                }

                Assert.True(
                    ExactPredicates.InSphere(a, b, c, d, mesh.Vertices[vertex]) <= 0,
                    $"vertex {vertex} is inside cell {cell}'s circumsphere"
                );
            }

            AssertAdjacencyIsMutual(mesh, cell);
        }
    }

    static bool IsCorner(DelaunayTetrahedralization mesh, int cell, int vertex) {
        for (var slot = 0; slot < 4; slot++) {
            if (mesh.CellVertices[(cell * 4) + slot] == vertex) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Each neighbour link points back, and the two cells agree about which triangle they
    ///     share.
    /// </summary>
    static void AssertAdjacencyIsMutual(DelaunayTetrahedralization mesh, int cell) {
        for (var face = 0; face < 4; face++) {
            var neighbour = mesh.CellNeighbours[(cell * 4) + face];

            if (neighbour < 0) {
                continue;
            }

            var back = 0;
            while (back < 4 && mesh.CellNeighbours[(neighbour * 4) + back] != cell) {
                back++;
            }

            Assert.True(back < 4, $"cell {neighbour} does not point back at {cell}");

            var mine = FaceVertices(mesh, cell, face);
            var theirs = FaceVertices(mesh, neighbour, back);

            Array.Sort(mine);
            Array.Sort(theirs);
            Assert.Equal(mine, theirs);
        }
    }

    static int[] FaceVertices(DelaunayTetrahedralization mesh, int cell, int face) {
        var result = new List<int>(3);

        for (var slot = 0; slot < 4; slot++) {
            if (slot != face) {
                result.Add(mesh.CellVertices[(cell * 4) + slot]);
            }
        }

        return [.. result];
    }

    static double TotalVolume(DelaunayTetrahedralization mesh) {
        var total = 0d;

        for (var cell = 0; cell < mesh.CellCount; cell++) {
            var a = mesh.Vertices[mesh.CellVertices[cell * 4]];
            var b = mesh.Vertices[mesh.CellVertices[(cell * 4) + 1]];
            var c = mesh.Vertices[mesh.CellVertices[(cell * 4) + 2]];
            var d = mesh.Vertices[mesh.CellVertices[(cell * 4) + 3]];

            total += Math.Abs(Vector3.Dot(a - d, Vector3.Cross(b - d, c - d))) / 6d;
        }

        return total;
    }
}
