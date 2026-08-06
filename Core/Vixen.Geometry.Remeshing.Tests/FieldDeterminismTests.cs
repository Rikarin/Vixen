// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The field is byte-identical at any worker count and any batch size.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § B3 and § D14, which call this a gate rather than an aspiration.</b> Doc 08
///         caches compiled assets on a content hash and doc 22's crack-freedom is an equality over
///         shared boundary vertices, so a remesher whose output differs run to run makes every one of
///         those rebuild-unstable — a CI machine and a developer machine produce different meshlet
///         pages for the same file, and a golden fails for a reason nobody can attribute. R4 is where
///         the gate is <i>audited</i>; this is where R2 shows its own working.
///     </para>
///     <para>
///         ⚠ <b>Not close — the same bits</b>, which is what
///         <see cref="BitConverter.SingleToInt32Bits" /> below is for. A comparison with a tolerance
///         would pass on a solver that had a nondeterministic reduction in it and was merely
///         converging to the same place.
///     </para>
///     <para>
///         ⚠ <b>Worker count and batch size are two axes and testing one is not testing the other.</b>
///         The scheduler derives its default batch size from the worker count, so a run at sixteen and
///         a run at one differ in both — and a solver whose answer depended only on the batch size
///         would still pass. Both are varied, and independently. <c>SolverDeterminismTests</c> in
///         <c>Vixen.Geometry.Uv.Tests</c> is the model.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <c>JobScheduler.MaxSchedulers</c> is a process-wide
///         cap of eight that a scheduler only gives back on <see cref="IDisposable.Dispose" />, and
///         xunit runs test classes in parallel — so a leaked one here fails a test somewhere else,
///         later, in a class that has nothing to do with this. All the scheduler use in this assembly
///         is in this one class for that reason: one class is one collection, and one collection does
///         not run against itself.
///     </para>
/// </remarks>
public class FieldDeterminismTests {
    /// <summary>The two axes, crossed.</summary>
    public static TheoryData<int, int> Configurations {
        get {
            var data = new TheoryData<int, int>();

            foreach (var workers in new[] { 1, 4, 16 }) {
                foreach (var batch in new[] { 0, 1, 37, 512 }) {
                    data.Add(workers, batch);
                }
            }

            return data;
        }
    }

    /// <summary>A solve on the calling thread and a solve on <i>n</i> workers are the same bits.</summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_solve_is_the_same_bits_at_any_worker_count_and_batch_size(int workers, int batch) {
        var mesh = Fixture();
        var settings = new RemeshSettings();
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);

        var serial = CrossFieldSolver.Solve(mesh, settings, features, curvature);

        // ⚠ The guard `VfxParallelTests` records: a fixture that is too small or too uniform passes
        // this test by never having tested anything. The mesh has to be big enough that a colour's
        // sweep is genuinely split across the workers, and the field has to be non-trivial — a
        // constant field would be identical however it was computed.
        Assert.True(mesh.VertexCount > 1000, $"{mesh.VertexCount} vertices is not enough to split.");

        Assert.True(
            SingularityPass.Extract(mesh, serial).Count >= 8,
            "The field has no structure in it, so identical output proves nothing."
        );

        using var scheduler = new JobScheduler(workers);

        var parallel = CrossFieldSolver.Solve(mesh, settings, features, curvature, scheduler, batch);

        Assert.Equal(serial.Count, parallel.Count);

        for (var vertex = 0; vertex < serial.Count; vertex++) {
            var here = serial.Direction(vertex);
            var there = parallel.Direction(vertex);

            Assert.Equal(BitConverter.SingleToInt32Bits(here.X), BitConverter.SingleToInt32Bits(there.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(here.Y), BitConverter.SingleToInt32Bits(there.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(here.Z), BitConverter.SingleToInt32Bits(there.Z));
        }
    }

    /// <summary>Ten runs on the calling thread are ten identical answers.</summary>
    /// <remarks>
    ///     The other half of § D14: no clock, no seed, no hash-order iteration that decides anything.
    ///     A solver that read <c>Dictionary</c> order somewhere would fail here without a single thread
    ///     being involved.
    /// </remarks>
    [Fact]
    public void Ten_runs_are_ten_identical_answers() {
        var mesh = Fixture();
        var settings = new RemeshSettings();
        var features = FeatureDetector.Detect(mesh, settings);
        var curvature = CurvatureField.Build(mesh);
        var first = CrossFieldSolver.Solve(mesh, settings, features, curvature);

        for (var run = 1; run < 10; run++) {
            var again = CrossFieldSolver.Solve(
                mesh,
                new RemeshSettings(),
                FeatureDetector.Detect(mesh, new()),
                CurvatureField.Build(mesh)
            );

            for (var vertex = 0; vertex < first.Count; vertex++) {
                Assert.Equal(first.Direction(vertex), again.Direction(vertex));
            }
        }

        // And the placement pass, which has its own dictionaries, queues and sets in it.
        var placed = SingularityPass.Place(mesh, settings, features, curvature, first, out var cancelled);

        for (var run = 1; run < 5; run++) {
            var again = SingularityPass.Place(
                mesh,
                settings,
                features,
                curvature,
                CrossFieldSolver.Solve(mesh, settings, features, curvature),
                out var count
            );

            Assert.Equal(cancelled, count);

            for (var vertex = 0; vertex < placed.Count; vertex++) {
                Assert.Equal(placed.Direction(vertex), again.Direction(vertex));
            }
        }
    }

    /// <summary>The colouring is a colouring: no two neighbours share one.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes the parallel sweep legal in the first place, and it is worth checking
    ///     directly rather than inferring it from the bits matching.</b> Two nodes of the same colour
    ///     share no edge, so neither reads what the other writes — if that ever stopped being true the
    ///     bit-comparison above would start failing intermittently, on some machines, under load,
    ///     which is the worst possible way to find out.
    /// </remarks>
    [Fact]
    public void One_colour_is_a_set_of_independent_vertices() {
        var mesh = Fixture();
        var settings = new RemeshSettings();
        var level = CrossFieldSolver.Base(
            mesh,
            settings,
            FeatureDetector.Detect(mesh, settings),
            CurvatureField.Build(mesh),
            null
        );

        Assert.Equal(mesh.VertexCount, level.Count);
        Assert.True(level.Colours >= 4, $"{level.Colours} colours on a triangle mesh is suspiciously few.");

        var colour = new int[level.Count];

        Array.Fill(colour, -1);

        var seen = 0;

        for (var group = 0; group < level.Colours; group++) {
            for (var at = level.ColourStarts[group]; at < level.ColourStarts[group + 1]; at++) {
                colour[level.ColourOrder[at]] = group;
                seen++;

                // Ascending within a colour, which is what makes the sweep order — and therefore the
                // answer — a property of the input rather than of the scheduler.
                if (at > level.ColourStarts[group]) {
                    Assert.True(level.ColourOrder[at] > level.ColourOrder[at - 1]);
                }
            }
        }

        Assert.Equal(level.Count, seen);

        for (var node = 0; node < level.Count; node++) {
            for (var at = level.Starts[node]; at < level.Starts[node + 1]; at++) {
                Assert.NotEqual(colour[node], colour[level.Neighbours[at]]);
            }
        }
    }

    /// <summary>Big enough that a colour's sweep is genuinely split, and structured enough to be worth comparing.</summary>
    static ManifoldMesh Fixture() =>
        FieldFixtures.Condition(
            MeshShapes.Create(ShapeParameters.Default(ShapeKind.Capsule) with { Sides = 48, Steps = 24 })
        );
}
