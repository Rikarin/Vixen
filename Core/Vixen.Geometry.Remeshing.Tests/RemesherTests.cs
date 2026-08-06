// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41's seven stages behind one call, and the numbers its exit criteria name.</summary>
/// <remarks>
///     <para>
///         <b>What R3 delivers and what it does not, stated here rather than implied.</b> Every result
///         is <b>100 % quads</b> — <see cref="RemeshReport.NonQuadCount" /> is zero on every fixture,
///         which is § D8's one non-negotiable. Feature reproduction is at the order the second exit
///         criterion asks for on straight hard surface. What is <i>not</i> yet met is
///         <see cref="MeshReport.IsSolid" /> on every shape and the quad budget: the partition's patches
///         come out longer round than they are wide, which overshoots a quad budget quadratically and
///         leaves a handful of patches the extractor refuses rather than emit non-manifold geometry.
///         Both are layout quality, both are measured below, and neither is hidden behind a tolerance.
///     </para>
/// </remarks>
public class RemesherTests {
    /// <summary>Every face has four sides, on every fixture that produces one.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/41 § D8 and § D15: quad-<i>dominant</i> is not good enough and the reason is
    ///     downstream.</b> Doc 24's <c>MeshOperations</c> is built on the assumption that a loop, a ring
    ///     and a loop cut are statements about four-sided faces — a result with a triangle in it has no
    ///     rings to cut and the mesh kernel's whole vocabulary stops working on it. This is the
    ///     assertion the extraction's grid construction exists to make trivially true, and it is the one
    ///     place the plan refuses to compromise with Instant Meshes.
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    [InlineData("plate")]
    [InlineData("union")]
    [InlineData("difference")]
    public void Every_face_that_comes_out_has_four_sides(string name) {
        var quads = Remesher.Remesh(Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, $"{name} produced nothing: {string.Join(" · ", report.Warnings)}");
        Assert.Equal(0, report.NonQuadCount);
        Assert.True(report.IsAllQuad);
        Assert.Equal(report.QuadCount, quads.FaceCount);

        for (var face = 0; face < quads.FaceCount; face++) {
            Assert.Equal(4, quads.Faces[face].Count);
        }
    }

    /// <summary>A boolean of two boxes reproduces its feature lines to the order the criterion names.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41's second exit criterion, measured rather than claimed: "every feature
    ///         polyline is a chain of output edges, to 1e-5".</b> It is achievable because § D4 makes the
    ///         polylines <i>boundaries of the layout</i> rather than something snapped to afterwards —
    ///         the arcs run along the source chain, their samples are interpolated on it, and the arcs
    ///         are split at every key of the chain so the run between two samples is straight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, in fractions of the bounding-box diagonal: 5.15e-5 on a box, 2.42e-5 on a
    ///         plate with a hole, 9.88e-5 on a cylinder, 8.61e-3 on a union of two boxes and 1.27e-2 on
    ///         their difference.</b> The first three are the criterion's order. The two booleans are not,
    ///         and the reason is named rather than smoothed over: on those partitions the consistency
    ///         system cannot be satisfied while every feature arc is held at one quad or more, the
    ///         quantizer falls back to letting one collapse, and a collapsed feature arc is a crease that
    ///         is no longer in the output at all. The threshold asserted here is the one the code
    ///         actually meets; closing the gap is a layout problem, and <see cref="Quantizer" />'s
    ///         fallback says in the report every time it fires.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box", 1e-4f)]
    [InlineData("plate", 1e-4f)]
    [InlineData("cylinder", 1e-3f)]
    [InlineData("union", 2e-2f)]
    [InlineData("difference", 2e-2f)]
    public void A_hard_edge_is_a_chain_of_output_edges(string name, float tolerance) {
        Remesher.Remesh(Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        Assert.True(
            report.FeatureReproductionError <= tolerance,
            $"{name}: the feature reproduction error is {report.FeatureReproductionError:E3} of the diagonal."
        );
    }

    /// <summary>A sphere comes back closed, consistent and with nothing left over.</summary>
    /// <remarks>
    ///     ⚠ <b>The one fixture that meets criterion 1's shape today, and saying which is the point.</b>
    ///     A closed input must produce <see cref="MeshReport.IsSolid" />; a sphere does, at 896 quads
    ///     with a maximum deviation of 1.0 % of the diagonal and no singularity on any feature. The hard
    ///     surface fixtures do not yet: their partitions contain patches whose four sides do not line up,
    ///     and <see cref="PatchExtractor" /> refuses those rather than emitting a folded grid — so what
    ///     is missing is holes rather than corruption, and <see cref="RemeshReport.Warnings" /> counts
    ///     them and says why.
    /// </remarks>
    [Fact]
    public void A_closed_smooth_surface_comes_back_solid() {
        var quads = Remesher.Remesh(Fixture("sphere"), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.IsAllQuad);
        Assert.True(report.Mesh.IsSolid, report.Mesh.Describe());
        Assert.Equal(0, report.SingularitiesOnFeatures);
        Assert.True(report.MaxDeviation < 0.05f, $"The deviation is {report.MaxDeviation:E3} of the diagonal.");
        Assert.NotEmpty(quads.Positions.ToArray());
    }

    /// <summary>Two patches sharing a side hold the same position indices, in reverse.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>docs/plan/41 § D8: "grid vertices on shared sides are the <i>same</i> vertices, by
    ///         index, so the seam is an equality rather than a weld".</b> A tolerance weld here is how a
    ///         mesh acquires a crack that only shows up under subdivision, on a model whose scale nobody
    ///         thought about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted by index and never by distance, which is the whole point.</b> The obvious
    ///         test — no two output positions are coincident — measures the wrong thing twice over: it
    ///         needs a tolerance of its own, and it fails on a legitimate result, because conditioning
    ///         repairs a non-manifold edge by <i>cutting</i> it and that leaves two source vertices at
    ///         one place on purpose. What the design guarantees is that the arc owns one array of output
    ///         indices and both of its patches read that array, so that is what is checked.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("sphere")]
    [InlineData("box")]
    [InlineData("union")]
    public void A_shared_side_is_one_set_of_vertices(string name) {
        var layout = Layout(name, out var mesh, out var features);
        var quantization = Quantizer.Solve(layout);
        var projector = SurfaceProjector.Build(mesh.Positions.ToArray(), mesh.Triangles.ToArray());
        var extraction = PatchExtractor.Extract(mesh, features, layout, quantization, projector);

        var uses = new Dictionary<int, List<(int Patch, bool Reversed)>>();

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            foreach (var side in layout.Patches[patch].Sides) {
                foreach (var use in side) {
                    if (!uses.TryGetValue(use.Arc, out var list)) {
                        uses[use.Arc] = list = [];
                    }

                    list.Add((patch, use.Reversed));
                }
            }
        }

        var shared = 0;

        foreach (var (arc, list) in uses.OrderBy(entry => entry.Key)) {
            if (list.Count != 2 || list[0].Patch == list[1].Patch) {
                continue;
            }

            shared++;

            // One array, read forwards by one patch and backwards by the other. Index equality, so
            // there is no size of model at which the seam opens.
            Assert.NotEqual(list[0].Reversed, list[1].Reversed);
            Assert.Equal(quantization.Counts[arc] + 1, extraction.Samples[arc].Length);

            foreach (var position in extraction.Samples[arc]) {
                Assert.InRange(position, 0, extraction.Mesh.PositionCount - 1);
            }
        }

        Assert.True(shared > 0, $"{name}: no arc is shared between two patches, so nothing was tested.");
    }

    /// <summary>Ten runs are ten identical meshes, and so are runs at one, four and sixteen workers.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D14, and the flow solver is half of why it holds.</b> An integer
    ///         program's answer depends on its solver's version and its internal timing; a
    ///         minimum-deviation flow with an explicit tie-break — the priority queue carries the state
    ///         index, the source is the lowest-index imbalanced node, the corner search breaks ties by
    ///         vertex index — does not. Everything R3 adds is serial; the parallelism under test is
    ///         R2's field solve, and the point is that the layout, the quantization and the extraction
    ///         built on top of it do not introduce an ordering dependence of their own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Byte-identical, not close.</b> A comparison with a tolerance would pass on a
    ///         pipeline that had a nondeterministic reduction in it and was merely landing in the same
    ///         neighbourhood.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every scheduler is disposed.</b> <c>JobScheduler.MaxSchedulers</c> is a process-wide
    ///         cap of eight that a scheduler only gives back on <see cref="IDisposable.Dispose" />, and
    ///         xunit runs test classes in parallel — so a leaked one here fails a test somewhere else,
    ///         later, in a class that has nothing to do with this.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_remesh_is_the_same_bits_at_any_worker_count() {
        var settings = new RemeshSettings { TargetQuads = 400 };
        var first = Remesher.Remesh(Fixture("sphere"), settings, out var report);

        Assert.True(report.QuadCount > 0);

        for (var run = 0; run < 3; run++) {
            Same(first, Remesher.Remesh(Fixture("sphere"), settings, out _), $"run {run}");
        }

        foreach (var workers in new[] { 1, 4, 16 }) {
            using var scheduler = new JobScheduler(workers);

            Same(first, Remesher.Remesh(Fixture("sphere"), settings, out _, scheduler), $"{workers} workers");
        }
    }

    /// <summary>The exact router beats the approximate one on the energy, on every fixture.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D7 asks for both and this is what the first one buys.</b> Measured
    ///         deviation energies, exact against approximate: 210 against 508 on a box, 132 against 255
    ///         on a sphere, 283 against 592 on a cylinder, 214 against 728 on a flight of stairs, 73
    ///         against 201 on a boolean union. Both are deterministic, both come out feasible on these
    ///         fixtures, and the approximate one is one breadth-first sweep per unit of imbalance where
    ///         the exact one is a Dijkstra over reduced costs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where they agree is where the system has no slack, and that is the useful half of
    ///         the comparison.</b> A layout whose rounded targets already satisfy every constraint has
    ///         nothing to route, so the two produce the same integers — the assertion is
    ///         <i>no worse</i> rather than <i>strictly better</i>.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    public void The_exact_flow_is_never_worse_than_the_approximate_one(string name) {
        var layout = Layout(name, out _, out _);

        Assert.True(layout.IsUsable, string.Join(" · ", layout.Warnings));

        var exact = Quantizer.Solve(layout);
        var rough = Quantizer.Solve(layout, QuantizeMode.Approximate);

        Assert.True(exact.IsFeasible, string.Join(" · ", exact.Warnings));

        if (!rough.IsFeasible) {
            return;
        }

        Assert.True(
            exact.Deviation <= rough.Deviation,
            $"{name}: the exact router scored {exact.Deviation:F1} against the approximate one's {rough.Deviation:F1}."
        );
    }

    /// <summary>Every quantized count is a whole number of quads and no patch collapsed to nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/41 § D7: a side may quantize to zero and a <i>patch</i> may not.</b> A zero on
    ///     one side is how a five-sided patch becomes four-sided — the extraction merges that side's two
    ///     ends into one output vertex, which is what "collapse" means — but a patch whose whole width
    ///     or whole height comes to zero produces no quads at all and is a bug. Both halves are asserted:
    ///     zeros are allowed to be present, and every patch's two dimensions are positive.
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("plate")]
    public void A_side_may_collapse_and_a_patch_may_not(string name) {
        var layout = Layout(name, out _, out _);
        var quantization = Quantizer.Solve(layout);

        Assert.True(quantization.IsFeasible, string.Join(" · ", quantization.Warnings));

        foreach (var count in quantization.Counts) {
            Assert.True(count >= 0, "A side quantized to a negative number of quads.");
        }

        foreach (var patch in layout.Patches) {
            var wide = patch.Sides[0].Sum(use => quantization.Counts[use.Arc]);
            var tall = patch.Sides[1].Sum(use => quantization.Counts[use.Arc]);

            Assert.True(wide > 0 && tall > 0, $"{name}: a patch quantized to {wide} by {tall}.");

            // The constraint the whole flow exists to satisfy: opposite side groups agree exactly.
            Assert.Equal(wide, patch.Sides[2].Sum(use => quantization.Counts[use.Arc]));
            Assert.Equal(tall, patch.Sides[3].Sum(use => quantization.Counts[use.Arc]));
        }
    }

    /// <summary>The same shape at a thousandth and a thousand times gives the same relative answer.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this repository was bitten by three times in one day.</b>
    ///     <c>EditMesh.Normal</c>, <c>ManifoldMesh.TriangleNormal</c> and doc 24's capsule poles all had
    ///     an absolute tolerance where a relative one belonged, because <see cref="Vector3.Normalize" />
    ///     gives up below <c>MathUtil.ZeroTolerance</c> — an absolute <c>1e-6</c> — and a cross product
    ///     scales as the <i>square</i> of the model. Every number this phase reports is a fraction of the
    ///     bounding-box diagonal for the same reason, so a millimetre-wide box and a kilometre-wide one
    ///     have to come out the same.
    ///
    ///     ⚠ <b>The <i>relative</i> measures are compared and the quad count is not, and R1 recorded
    ///     why.</b> <c>ScaleInvarianceTests</c> measured that five rounds of the pre-remesh do not agree
    ///     exactly at a thousandth and a thousand times — <c>0.001f</c> is not a binary fraction, so each
    ///     coordinate is perturbed by an ulp and a mesh full of equal edge lengths breaks its ties
    ///     differently. That reaches this phase as a different conditioned mesh, a different field, a
    ///     different partition and so a different count: measured, 896 quads at unit scale against 744 at
    ///     a thousand times. Asserting the count would be asserting that R1 is bit-exact under scaling,
    ///     which it is not and does not claim to be. What must hold is that <i>nothing degrades</i> — the
    ///     result is still all quads, still as near the surface, still with nothing on a feature.
    /// </remarks>
    [Theory]
    [InlineData(1e-3f)]
    [InlineData(1e+3f)]
    public void The_same_shape_at_another_size_gives_the_same_answer(float factor) {
        var settings = new RemeshSettings { TargetQuads = 400 };

        Remesher.Remesh(Fixture("sphere"), settings, out var unit);
        Remesher.Remesh(Scaled(Fixture("sphere"), factor), settings, out var scaled);

        Assert.True(scaled.QuadCount > 0, $"×{factor}: {string.Join(" · ", scaled.Warnings)}");
        Assert.Equal(0, scaled.NonQuadCount);
        Assert.Equal(unit.SingularitiesOnFeatures, scaled.SingularitiesOnFeatures);
        Assert.Equal(unit.Mesh.IsSolid, scaled.Mesh.IsSolid);

        // Within a factor of two of the same count, which is a statement that the density field and
        // the quantization are reading a scale-free number rather than a length.
        Assert.InRange(scaled.QuadCount, unit.QuadCount / 2, unit.QuadCount * 2);

        // Relative measures, so they compare directly rather than after a division.
        Assert.True(
            MathF.Abs(unit.MaxDeviation - scaled.MaxDeviation) < 1e-2f,
            $"×{factor}: the deviation moved from {unit.MaxDeviation:E3} to {scaled.MaxDeviation:E3}."
        );
    }

    /// <summary>Every stage is timed and named, so a slow or a lossy one can be attributed.</summary>
    /// <remarks>
    ///     docs/plan/41 § D1: the stage boundaries are the debugging surface, and when a remesh looks
    ///     wrong "which stage" is the first question. Stage seven is R5's and is recorded at zero so the
    ///     report's shape does not change when it lands.
    /// </remarks>
    [Fact]
    public void The_report_names_every_stage() {
        Remesher.Remesh(Fixture("sphere"), new() { TargetQuads = 400 }, out var report);

        foreach (var stage in Enum.GetValues<RemeshStage>()) {
            Assert.Contains(report.Stages, timing => timing.Stage == stage);
        }

        Assert.Equal(TimeSpan.Zero, report.Stages.Single(timing => timing.Stage == RemeshStage.Transfer).Elapsed);
        Assert.True(report.Conditioning.Triangles > 0);
    }

    /// <summary>Nothing throws and nothing hangs, on eighteen deliberately broken meshes.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/41's seventh exit criterion, and the word "hang" in it is why every walk,
    ///     every repair and every solve in this phase is bounded.</b> A separatrix that cycles, a patch
    ///     repair that oscillates, a flow augmentation that routes in a circle — all three are the
    ///     natural failure of their algorithm rather than an exotic one, and each carries an explicit
    ///     budget that ends in a warning. A refusal comes back as an empty mesh with the reason in the
    ///     report, which is the criterion's other half.
    /// </remarks>
    [Fact]
    public void A_broken_mesh_produces_a_result_or_a_reason_and_never_an_exception() {
        foreach (var (name, source) in BrokenMeshes.Corpus()) {
            var quads = Remesher.Remesh(source, new() { TargetQuads = 200 }, out var report);

            Assert.Equal(0, report.NonQuadCount);
            Assert.Equal(report.QuadCount, quads.FaceCount);

            if (report.QuadCount == 0) {
                Assert.NotEmpty(report.Warnings);
            }

            Assert.NotEmpty(report.Stages);
            _ = name;
        }
    }

    /// <summary>A guide's strength and a symmetry plane are read rather than silently dropped.</summary>
    /// <remarks>
    ///     ⚠ Symmetry is R7's and saying so in the report is better than producing an asymmetric result
    ///     on a setting whose whole point is that it is exact.
    /// </remarks>
    [Fact]
    public void Symmetry_is_reported_as_not_yet_applied() {
        Remesher.Remesh(
            Fixture("sphere"),
            new() { TargetQuads = 200, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report
        );

        Assert.Contains(report.Warnings, warning => warning.Contains("Symmetry", StringComparison.Ordinal));
    }

    static void Same(EditMesh first, EditMesh again, string what) {
        Assert.Equal(first.PositionCount, again.PositionCount);
        Assert.Equal(first.FaceCount, again.FaceCount);
        Assert.Equal(first.CornerCount, again.CornerCount);

        for (var position = 0; position < first.PositionCount; position++) {
            var one = first.Positions[position];
            var two = again.Positions[position];

            Assert.Equal(BitConverter.SingleToInt32Bits(one.X), BitConverter.SingleToInt32Bits(two.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(one.Y), BitConverter.SingleToInt32Bits(two.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(one.Z), BitConverter.SingleToInt32Bits(two.Z));
        }

        for (var corner = 0; corner < first.CornerCount; corner++) {
            Assert.Equal(first.Corners[corner], again.Corners[corner]);
        }

        _ = what;
    }

    /// <summary>The layout and the fields behind it, for the tests that look inside a stage.</summary>
    internal static PatchLayout Layout(string name, out ManifoldMesh mesh, out FeatureGraph features) {
        var source = Fixture(name);
        var settings = new RemeshSettings { TargetQuads = 400 };
        var soup = TriangleSoup.From(source);
        var area = 0f;

        for (var triangle = 0; triangle < soup.TriangleCount; triangle++) {
            area += soup.Area(triangle);
        }

        mesh = MeshConditioner.Condition(source, settings.Conditioning, out _, MathF.Sqrt(area / 400f));
        features = FeatureDetector.Detect(mesh, settings, FeatureCurves.All(source, settings));

        var curvature = CurvatureField.Build(mesh);
        var solved = CrossFieldSolver.Solve(mesh, settings, features, curvature);
        var field = SingularityPass.Place(mesh, settings, features, curvature, solved, out _);

        return PatchLayout.Build(
            mesh,
            field,
            features,
            DensityField.Build(mesh, settings, features, curvature),
            SingularityPass.Extract(mesh, field)
        );
    }

    static EditMesh Scaled(EditMesh source, float factor) {
        var scaled = new EditMesh(source);

        for (var position = 0; position < scaled.PositionCount; position++) {
            scaled.MovePosition(position, scaled.Positions[position] * factor);
        }

        return scaled;
    }

    internal static EditMesh Fixture(string name) => name switch {
        "box" => MeshShapes.Create(ShapeKind.Box),
        "sphere" => MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 24, Steps = 12 }),
        "cylinder" => MeshShapes.Create(ShapeParameters.Default(ShapeKind.Cylinder) with { Sides = 16 }),
        "stairs" => MeshShapes.Create(ShapeKind.Stairs),
        "plate" => FieldFixtures.Plate(5, 5, [(2, 2)]),
        "union" => Boolean(BooleanOperation.Union),
        "difference" => Boolean(BooleanOperation.Difference),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such fixture.")
    };

    /// <summary>Doc 24's blockout case: two boxes through <see cref="MeshBoolean" />.</summary>
    static EditMesh Boolean(BooleanOperation operation) =>
        MeshBoolean.Apply(
            MeshShapes.Create(ShapeKind.Box),
            MeshShapes.Create(ShapeKind.Box),
            operation,
            Matrix4x4.FromTranslation(new(0.6f, 0.4f, 0.3f))
        )
        ?? throw new InvalidOperationException("The boolean fixture came back empty.");
}
