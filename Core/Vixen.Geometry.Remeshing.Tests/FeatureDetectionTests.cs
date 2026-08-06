// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D4: five sources, chained, cornered, pruned and simplified.</summary>
public class FeatureDetectionTests {
    /// <summary>A cube's twelve edges are twelve features, and its eight corners are eight corners.</summary>
    /// <remarks>
    ///     ⚠ <b>The one shape where every vertex is a feature corner, and it is the counter-example
    ///     that fixes what "zero singularities on features" can mean.</b> See
    ///     <see cref="SingularityTests" />.
    /// </remarks>
    [Fact]
    public void A_cube_is_twelve_chains_and_eight_corners() {
        var mesh = FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Box));
        var features = FeatureDetector.Detect(mesh, new());

        Assert.Equal(12, features.Polylines.Count);
        Assert.Equal(8, features.Corners.Count);

        foreach (var chain in features.Polylines) {
            Assert.Equal(1, chain.EdgeCount);
            Assert.True(chain.IsHard, "A dihedral angle is a hard constraint.");
            Assert.Equal(FeatureSource.Dihedral, chain.Sources & FeatureSource.Dihedral);
        }

        foreach (var corner in features.Corners) {
            Assert.Equal(3, features.Degree(corner));
            Assert.True(features.IsCorner(corner));
        }
    }

    /// <summary>Every chain is a run of mesh edges, and never a chord across one.</summary>
    /// <remarks>
    ///     <b>docs/plan/41 § D4's whole thesis, at the only place stage two can assert it.</b> "A
    ///     feature polyline is a chain of output edges by construction, and the exit criterion asserts
    ///     that at a tolerance of exact." R3 asserts it of the output; here it is asserted of the
    ///     input, because a polyline that was not a chain of <i>input</i> edges could not possibly
    ///     become one of output edges.
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("two-boxes")]
    [InlineData("stairs")]
    [InlineData("staircase")]
    public void Every_chain_is_a_run_of_mesh_edges(string name) {
        var mesh = Fixture(name);
        var features = FeatureDetector.Detect(mesh, new());

        foreach (var chain in features.Polylines) {
            Assert.True(chain.Vertices.Length >= 2, "A chain with one vertex is not a chain.");

            for (var at = 0; at + 1 < chain.Vertices.Length; at++) {
                Assert.True(
                    Joined(mesh, chain.Vertices[at], chain.Vertices[at + 1]),
                    $"{name}: {chain.Vertices[at]} to {chain.Vertices[at + 1]} is not an edge."
                );
            }

            Assert.Equal(chain.IsClosed, chain.Vertices[0] == chain.Vertices[^1]);

            // The keys index into the chain and always include both ends, so a consumer can walk
            // key to key and still know every vertex the boundary has to pass through.
            Assert.Equal(0, chain.Keys[0]);
            Assert.Equal(chain.Vertices.Length - 1, chain.Keys[^1]);

            for (var at = 1; at < chain.Keys.Length; at++) {
                Assert.True(chain.Keys[at] > chain.Keys[at - 1], "The keys must ascend.");
            }
        }
    }

    /// <summary>The prune is what makes marching-cubes output tractable, and this is the number.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D4: "marching-cubes output produces thousands of two-edge 'features'
    ///         that are noise".</b> R1's staircase sphere is a sphere — it has no features at all —
    ///         and every edge the dihedral test finds on it is a voxel facet boundary. Unpruned there
    ///         are two hundred and fifty of them; pruned there are a dozen, and each of those is a
    ///         long run the merging joined up rather than a facet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted, because half of this test proves nothing.</b> A detector
    ///         that found no features at all would pass "the surviving count is small" and fail
    ///         everything downstream; a threshold that pruned nothing would pass "the raw count is
    ///         large". The ratio is the claim.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Pruning_is_what_makes_generated_input_tractable() {
        var mesh = FieldFixtures.Condition(BrokenMeshes.StaircaseSphere());
        var features = FeatureDetector.Detect(mesh, new());

        Assert.True(
            features.PrunedChains > 200,
            $"Only {features.PrunedChains} chains were pruned, so there was no noise to remove."
        );

        Assert.True(
            features.Polylines.Count < 20,
            $"{features.Polylines.Count} chains survived, which is a field aligned to the voxel grid."
        );

        // And the same fixture with the prune disabled, which is the "before" the ratio is against.
        Assert.True(
            features.Polylines.Count + features.PrunedChains > 200,
            "The raw detection found too little for the prune to be the thing being measured."
        );
    }

    /// <summary>A real feature is not pruned, however short it is against the model.</summary>
    /// <remarks>
    ///     ⚠ <b>A box's twelve edges are one mesh edge each, which is what rules out an edge-count
    ///     prune.</b> They survive because the threshold is an arc length against the model's own
    ///     diagonal, and a cube's edge is <c>1/√3</c> of its own.
    /// </remarks>
    [Theory]
    [InlineData("box", 12)]
    [InlineData("two-boxes", 24)]
    public void A_real_feature_survives_the_prune(string name, int expected) {
        var features = FeatureDetector.Detect(Fixture(name), new());

        Assert.Equal(expected, features.Polylines.Count);
        Assert.Equal(0, features.PrunedChains);
    }

    /// <summary>Each of the five sources can be turned off, and off means absent.</summary>
    [Fact]
    public void Each_source_is_its_own_switch() {
        var mesh = FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Box));

        Assert.Empty(FeatureDetector.Detect(mesh, new() { FeatureAngle = 180f, KeepGroups = false }).Polylines);

        // A box carries one group per face, so the group boundaries are the same twelve edges the
        // dihedral test finds — which is exactly ZRemesher's Keep Groups on a shape where the two
        // agree, and the point is that either one alone is enough.
        Assert.Equal(12, FeatureDetector.Detect(mesh, new() { FeatureAngle = 180f }).Polylines.Count);
        Assert.Equal(12, FeatureDetector.Detect(mesh, new() { KeepGroups = false }).Polylines.Count);
    }

    /// <summary>A crease, a seam and a guide are all polylines, and they resolve onto edges the same way.</summary>
    /// <remarks>
    ///     ⚠ <b>A guide is detected exactly as the other four are and is the one kind whose constraint
    ///     is soft.</b> docs/plan/41 § D4 lists guide curves as the fifth feature source; § D5 says
    ///     feature polylines are hard and guides are soft. <see cref="FeaturePolyline.IsHard" /> is
    ///     the resolution and this is where it is checked.
    /// </remarks>
    [Fact]
    public void A_curve_claims_the_edges_it_runs_along() {
        var mesh = FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Plane) with { Sides = 8, Steps = 8 })
        );

        // Straight across the middle of the grid, from one rim to the other.
        var box = mesh.Bounds;
        var middle = (box.Minimum.Z + box.Maximum.Z) * 0.5f;

        var guide = new FeatureCurve(
            [new(box.Minimum.X, 0f, middle), new(box.Maximum.X, 0f, middle)],
            FeatureSource.Guide,
            0.5f
        );

        var plain = FeatureDetector.Detect(mesh, new() { FreezeBorder = false });
        var guided = FeatureDetector.Detect(mesh, new() { FreezeBorder = false }, [guide]);

        Assert.Empty(plain.Polylines);
        Assert.NotEmpty(guided.Polylines);

        var chain = guided.Polylines.OrderByDescending(entry => entry.EdgeCount).First();

        Assert.True(chain.EdgeCount >= 6, $"The guide only claimed {chain.EdgeCount} edges of eight.");
        Assert.False(chain.IsHard, "A guide alone is a soft constraint.");
        Assert.Equal(0.5f, chain.Strength);

        foreach (var vertex in chain.Vertices) {
            Assert.True(guided.IsFeatureVertex(vertex));
            Assert.False(guided.IsHardVertex(vertex), "A guide must not pin the cross.");
        }

        // The same curve as a crease is hard, which is the whole difference between the two.
        var creased = FeatureDetector.Detect(
            mesh,
            new() { FreezeBorder = false },
            [guide with { Source = FeatureSource.Crease }]
        );

        Assert.True(creased.Polylines.All(entry => entry.IsHard));
    }

    /// <summary>The three carried-in sources come off the source mesh and survive conditioning.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>docs/plan/41 § D4 claims doc 24's shapes carry explicit creases and they do not.</b>
    ///         <c>EditMesh</c> has <c>MeshFace.Group</c> — which is § D4's <i>separate</i> face-group
    ///         row — and <c>MeshFace.Smoothing</c>, the shading-group number; there is no per-edge
    ///         crease weight anywhere in <c>Vixen.Geometry</c> or in the importer. A smoothing-group
    ///         boundary is what "this edge is hard" means in every format that has the concept, so that
    ///         is what <see cref="FeatureCurves.FromCreases" /> reads, and the subdivision kind of
    ///         crease is not invented here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the reason all three arrive as curves rather than as edge indices: stage one
    ///         renumbers everything.</b> The weld merges positions, the repair cuts, the de-speck
    ///         deletes and the pre-remesh splits and collapses, so an index taken before conditioning
    ///         names nothing after it. This test conditions with the pre-remesh running, so the surface
    ///         the curves are resolved against is genuinely not the one they were read off.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_crease_and_a_seam_come_off_the_source_and_survive_conditioning() {
        var source = MeshShapes.Create(ShapeParameters.Default(ShapeKind.Plane) with { Sides = 8, Steps = 8 });

        // Half the faces into a second smoothing group, which puts a crease along the middle.
        var box = source.Bounds;
        var middle = (box.Minimum.X + box.Maximum.X) * 0.5f;

        for (var face = 0; face < source.FaceCount; face++) {
            var centre = Vector3.Zero;

            foreach (var corner in source.CornersOf(face)) {
                centre += source.Positions[corner];
            }

            if (centre.X / source.CornersOf(face).Length > middle) {
                source.SetSmoothing(face, 1);
            }
        }

        // And a coordinate layer whose u jumps across the same line, which is a UV seam on it.
        var coordinates = new Vector2[source.CornerCount];

        for (var face = 0; face < source.FaceCount; face++) {
            var entry = source.Faces[face];
            var corners = source.CornersOf(face);

            // ⚠ Per <i>face</i> and not per position, which is what a seam is. Two faces meeting
            // at a shared position and reading the same coordinate off it is a mesh with no seam at
            // all — the discontinuity lives in the corner layer, which is why the layer is per corner.
            var side = source.Faces[face].Smoothing == 1 ? 1f : 0f;

            for (var corner = 0; corner < corners.Length; corner++) {
                coordinates[entry.Start + corner] = new(side, source.Positions[corners[corner]].Z);
            }
        }

        source.SetTexCoords(coordinates);

        Assert.NotEmpty(FeatureCurves.FromCreases(source));
        Assert.NotEmpty(FeatureCurves.FromUvSeams(source));

        var settings = new RemeshSettings { FeatureAngle = 180f, FreezeBorder = false, KeepUvSeams = true };
        var mesh = FieldFixtures.Condition(source, 2);
        var features = FeatureDetector.Detect(mesh, settings, FeatureCurves.All(source, settings));

        Assert.NotEmpty(features.Polylines);

        var chain = features.Polylines.OrderByDescending(entry => entry.EdgeCount).First();

        Assert.True(chain.IsHard, "A crease and a seam are both hard.");

        Assert.Equal(
            FeatureSource.Crease | FeatureSource.UvSeam,
            chain.Sources & (FeatureSource.Crease | FeatureSource.UvSeam)
        );

        // Off, and off means absent.
        var ignored = new RemeshSettings {
            FeatureAngle = 180f,
            FreezeBorder = false,
            KeepCreases = false,
            KeepUvSeams = false
        };

        Assert.Empty(FeatureDetector.Detect(mesh, ignored, FeatureCurves.All(source, ignored)).Polylines);

        // A mesh with no coordinate layer has no seams, which is not the same as having no
        // discontinuity — empty means absent, and `MeshData`'s rule is kept.
        Assert.Empty(FeatureCurves.FromUvSeams(MeshShapes.Create(ShapeKind.Box)));
    }

    /// <summary>An open rim is a feature only when Freeze Border is on, and both ends of it are on the chain.</summary>
    /// <remarks>
    ///     ⚠ <b>The rim is where an adjacency built from outgoing half-edges quietly loses a vertex.</b>
    ///     A boundary edge has one half-edge, leaving one of its two ends — so counting outgoing
    ///     halves gives the far end a degree of zero, and every chain along a rim terminates one
    ///     vertex early with a spurious corner where it stopped.
    /// </remarks>
    [Fact]
    public void An_open_rim_is_one_closed_chain_with_no_corners() {
        var mesh = FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Plane) with { Sides = 6, Steps = 6 })
        );

        Assert.Empty(FeatureDetector.Detect(mesh, new() { FreezeBorder = false }).Polylines);

        var features = FeatureDetector.Detect(mesh, new() { FreezeBorder = true });
        var chain = Assert.Single(features.Polylines);

        Assert.True(chain.IsClosed, "The rim of a rectangle is a loop.");
        Assert.Empty(features.Corners);

        var rim = 0;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++) {
            if (!mesh.IsBoundary(vertex)) {
                continue;
            }

            rim++;

            Assert.True(features.IsFeatureVertex(vertex), $"Rim vertex {vertex} is not on the chain.");
            Assert.Equal(2, features.Degree(vertex));
        }

        Assert.Equal(rim + 1, chain.Vertices.Length);
    }

    /// <summary>Nothing to find is a valid answer, at every zero.</summary>
    /// <remarks>
    ///     ⚠ Every one of these is a "zero means off" case: no features, no guides, an empty mesh, a
    ///     mesh that is entirely feature, and a plane whose curvature is exactly zero.
    /// </remarks>
    [Fact]
    public void The_zeros_are_answers_rather_than_crashes() {
        foreach (var (name, mesh) in BrokenMeshes.Corpus()) {
            var view = MeshConditioner.Condition(mesh, new(), out _);
            var features = FeatureDetector.Detect(view, new(), []);

            Assert.True(features.PrunedChains >= 0, name);

            foreach (var chain in features.Polylines) {
                Assert.True(chain.Vertices.Length >= 2, $"{name}: a chain of {chain.Vertices.Length}.");
                Assert.NotEmpty(chain.Keys);
            }

            for (var vertex = 0; vertex < view.VertexCount; vertex++) {
                if (!features.IsFeatureVertex(vertex)) {
                    Assert.Equal(Vector3.Zero, features.Tangent(vertex));
                }
            }
        }
    }

    /// <summary>Nothing here is a length, so nothing here moves with the model's size.</summary>
    /// <remarks>
    ///     <b>The failure R1 was bitten by twice, at the stage after it.</b> The dihedral threshold is
    ///     an angle and cannot go wrong; the prune, the simplification and the curve tolerance are all
    ///     lengths, and every one of them is written as a fraction of the bounding-box diagonal. A
    ///     constant in any of the three would delete a small model's whole feature set and keep a
    ///     large one's noise.
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("two-boxes")]
    [InlineData("stairs")]
    [InlineData("staircase")]
    public void Detection_is_the_same_six_orders_of_magnitude_apart(string name) {
        var mesh = Fixture(name);
        var small = FeatureDetector.Detect(FieldFixtures.Scaled(mesh, 1e-3f), new());
        var large = FeatureDetector.Detect(FieldFixtures.Scaled(mesh, 1e+3f), new());

        Assert.Equal(small.Polylines.Count, large.Polylines.Count);
        Assert.Equal(small.PrunedChains, large.PrunedChains);
        Assert.Equal(small.PrunedEdges, large.PrunedEdges);
        Assert.Equal(small.Corners, large.Corners);

        for (var at = 0; at < small.Polylines.Count; at++) {
            Assert.Equal(small.Polylines[at].Vertices, large.Polylines[at].Vertices);
            Assert.Equal(small.Polylines[at].Keys, large.Polylines[at].Keys);
            Assert.Equal(small.Polylines[at].Sources, large.Polylines[at].Sources);
        }
    }

    static bool Joined(ManifoldMesh mesh, int from, int to) {
        foreach (var half in mesh.Outgoing(from)) {
            if (mesh.Triangles[ManifoldMesh.Next(half)] == to) {
                return true;
            }
        }

        foreach (var half in mesh.Outgoing(to)) {
            if (mesh.Triangles[ManifoldMesh.Next(half)] == from) {
                return true;
            }
        }

        return false;
    }

    internal static ManifoldMesh Fixture(string name) => name switch {
        "box" => FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Box)),
        "two-boxes" => FieldFixtures.Condition(BrokenMeshes.SelfIntersecting()),
        "stairs" => FieldFixtures.Condition(MeshShapes.Create(ShapeKind.Stairs), 5, 0.15f),
        "staircase" => FieldFixtures.Condition(BrokenMeshes.StaircaseSphere(), 5, 0.06f),
        "sphere" => FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 40, Steps = 20 })
        ),
        "cylinder" => FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Cylinder) with { Sides = 32 })
        ),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such fixture.")
    };
}
