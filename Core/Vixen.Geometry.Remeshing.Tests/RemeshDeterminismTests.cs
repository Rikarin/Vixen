// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § R4's gate, run over the whole pipeline rather than over one stage.</summary>
/// <remarks>
///     <para>
///         <b>§ R4 is where the solver design gets <i>audited</i> rather than where determinism gets
///         added.</b> <c>FieldDeterminismTests</c> sweeps R2's solve and <c>RemesherTests</c> compares
///         one run per worker count; neither is ten runs at three worker counts through all six stages
///         that exist, and an ordering dependence introduced by the layout, the quantization or the
///         extraction would survive both. If one of these fails it is a bug in R2 or R3 and not
///         something to relax here.
///     </para>
///     <para>
///         ⚠ <b>Byte-identical, not close</b> — <see cref="BitConverter.SingleToInt32Bits" /> below.
///         A comparison with a tolerance passes on a pipeline that has a nondeterministic reduction in
///         it and is merely landing in the same neighbourhood, which is the failure that makes a
///         content build rebuild-unstable without ever looking wrong.
///     </para>
///     <para>
///         ⚠ <b>The batch-size axis is <i>not</i> reachable through <see cref="Remesher.Remesh(Vixen.Geometry.EditMesh, RemeshSettings, out RemeshReport, Vixen.Core.Threading.JobScheduler?)" />, and
///         that is a gap rather than an omission.</b> <see cref="CrossFieldSolver.Solve" /> takes a
///         batch size and <c>Remesh</c> does not forward one, so the second axis
///         <c>FieldDeterminismTests</c> sweeps stops at the stage boundary. The field solve is the only
///         threaded stage today, so nothing is currently unswept — but the moment a second stage takes
///         the scheduler that stops being true, and closing it means an internal overload on
///         <c>Remesh</c> of the shape <c>UvUnwrap.All</c> already has.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <c>JobScheduler.MaxSchedulers</c> is a process-wide cap
///         of eight that a scheduler gives back only on <see cref="IDisposable.Dispose" />, and xunit
///         runs test classes in parallel — a leaked one fails a test somewhere else, later, in a class
///         that has nothing to do with this one. Sixteen workers is one scheduler, not sixteen.
///     </para>
/// </remarks>
public class RemeshDeterminismTests {
    /// <summary>How many times each configuration is re-run. § R4 asks for ten.</summary>
    const int Runs = 10;

    /// <summary>The worker counts § R4 names.</summary>
    public static TheoryData<int> Workers => [1, 4, 16];

    /// <summary>Ten runs at one worker count are one run, through every stage.</summary>
    /// <remarks>
    ///     ⚠ <b>The fixture is guarded before the comparison means anything</b>, which is
    ///     <c>VfxParallelTests</c>' rule. A remesh that refused produces an empty mesh, and two empty
    ///     meshes are identical — so a gate over a fixture that fails would pass by never having
    ///     compared anything. The guard asks for quads, for irregular vertices, and for a conditioned
    ///     mesh big enough that a colour's sweep is genuinely split across sixteen workers.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Workers))]
    public void Ten_runs_at_one_worker_count_are_one_run(int workers) {
        var settings = new RemeshSettings { TargetQuads = 400 };

        using var scheduler = new JobScheduler(workers);

        Assert.Equal(workers, scheduler.WorkerCount);

        var first = Remesher.Remesh(RemesherTests.Fixture("sphere"), settings, out var report, scheduler);

        Guard(report, first);

        for (var run = 1; run < Runs; run++) {
            var again = Remesher.Remesh(RemesherTests.Fixture("sphere"), settings, out var repeat, scheduler);

            SameBits(first, again, $"{workers} workers, run {run}");
            SameNumbers(report, repeat, $"{workers} workers, run {run}");
        }
    }

    /// <summary>Every worker count agrees with every other, rather than each with the serial leg.</summary>
    /// <remarks>
    ///     ⚠ <b>Each against the calling thread is a weaker claim than each against each.</b> Two
    ///     parallel legs can agree with a serial one on the mesh and disagree with each other on a
    ///     number the serial leg never reached — the comparison that catches that is the transitive one.
    ///     The schedulers are built and disposed one at a time; five alive at once is under the cap and
    ///     five more in a neighbouring class is not.
    /// </remarks>
    [Theory]
    [InlineData("sphere")]
    [InlineData("box")]
    [InlineData("plate")]
    public void Every_worker_count_agrees_with_every_other(string shape) {
        var settings = new RemeshSettings { TargetQuads = 400 };
        var answers = new List<(EditMesh Mesh, RemeshReport Report, string What)> {
            (Remesher.Remesh(RemesherTests.Fixture(shape), settings, out var serial), serial, "no scheduler")
        };

        Guard(serial, answers[0].Mesh);

        foreach (var workers in new[] { 1, 2, 4, 8, 16 }) {
            using var scheduler = new JobScheduler(workers);

            answers.Add(
                (
                    Remesher.Remesh(RemesherTests.Fixture(shape), settings, out var parallel, scheduler),
                    parallel,
                    $"{workers} workers"
                )
            );
        }

        foreach (var answer in answers) {
            SameBits(answers[0].Mesh, answer.Mesh, $"{shape}, {answer.What}");
            SameNumbers(answers[0].Report, answer.Report, $"{shape}, {answer.What}");
        }
    }

    /// <summary>A settings record built twice is the same remesh, on a mesh with features in it.</summary>
    /// <remarks>
    ///     The other half of § D14, and it needs no thread at all: a pipeline that read a
    ///     <c>Dictionary</c>'s order, a clock or a hash seed anywhere would fail this without a
    ///     scheduler ever being constructed. The boolean fixtures are here because their feature graphs
    ///     are the densest — every set, queue and dictionary in the layout is exercised by a mesh whose
    ///     creases actually branch.
    /// </remarks>
    [Theory]
    [InlineData("union")]
    [InlineData("difference")]
    [InlineData("stairs")]
    public void Ten_runs_on_the_calling_thread_are_one_run(string shape) {
        var first = Remesher.Remesh(RemesherTests.Fixture(shape), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, $"{shape}: {string.Join(" · ", report.Warnings)}");
        Assert.Equal(0, report.NonQuadCount);

        for (var run = 1; run < Runs; run++) {
            var again = Remesher.Remesh(RemesherTests.Fixture(shape), new() { TargetQuads = 400 }, out var repeat);

            SameBits(first, again, $"{shape}, run {run}");
            SameNumbers(report, repeat, $"{shape}, run {run}");
        }
    }

    /// <summary>Renumbering the input's positions, which a thread sweep cannot catch.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Measured, and it does not hold — this test records a real finding rather than a
    ///         property.</b> A vertex numbering is not a property of a surface: an importer, a weld and
    ///         a boolean all renumber a mesh routinely, and docs/plan/42 § D5's flattener chooses its
    ///         pins by <i>position</i> precisely so that the map does not move when they do. The
    ///         remesher has no such guard. Its graph colouring is greedy by index, its colour order is
    ///         ascending by index, and every tie-break below the layout ends at a vertex index — so a
    ///         permutation of the input is a different sweep order, a different converged field, a
    ///         different singularity placement and a different partition.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What that costs, on a 400-quad budget with one fixed permutation:</b> a sphere and
    ///         a plate come out <i>bit-identical</i>; a box goes from 2,678 quads to 2,607; a flight of
    ///         stairs from 3,353 to 3,465; and a cylinder from 2,730 to 1,571 with its worst deviation
    ///         going from <c>9.21e-3</c> to <c>1.55e-1</c>. The two that survive are the two whose
    ///         conditioning renumbers them back into a canonical order anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The finding got smaller, and the reason says something about where it came
    ///         from.</b> These numbers were 5,047 → 3,312 on a box and 3,836 → 1,720 on a cylinder, and
    ///         <i>a flight of stairs used to stop producing anything at all</i> under the permutation.
    ///         What changed is not this test's subject: once <see cref="PatchLayout" /> stopped leaving
    ///         dangling cuts, the partition stopped depending on which of several slits the flood
    ///         happened to walk first. A sensitivity that large was partly a defect wearing a
    ///         determinism costume — the residue below is the genuine index-order dependence, and a
    ///         cylinder is where it still bites.
    ///     </para>
    ///     <para>
    ///         So the assertion here is the one that must hold on <i>every</i> fixture — all quads, and
    ///         a result or a named reason — plus bit-equality on the two where it does hold, so that
    ///         losing it would be noticed. ⚠ <b>This is not a § D14 failure</b>: D14's gate is one input
    ///         at any thread count, and a renumbered mesh is a different input. It is a robustness
    ///         finding, and the honest place for it is a test that says so.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box", false)]
    [InlineData("sphere", true)]
    [InlineData("cylinder", false)]
    [InlineData("stairs", false)]
    [InlineData("plate", true)]
    public void A_renumbered_input_still_produces_quads_or_a_reason(string shape, bool invariant) {
        var settings = new RemeshSettings { TargetQuads = 400 };
        var source = RemesherTests.Fixture(shape);
        var reference = Remesher.Remesh(source, settings, out var before);
        var moved = Renumber(source, 0x51F3u);

        Assert.Equal(source.PositionCount, moved.PositionCount);
        Assert.Equal(source.FaceCount, moved.FaceCount);

        // The permutation actually permuted something, or the rest of this proves nothing.
        Assert.NotEqual(
            Enumerable.Range(0, source.PositionCount).Select(index => source.Positions[index]).ToArray(),
            Enumerable.Range(0, moved.PositionCount).Select(index => moved.Positions[index]).ToArray()
        );

        var renumbered = Remesher.Remesh(moved, settings, out var after);

        // The invariant half: whatever the numbering, the result is all quads and it either has faces
        // or it says in the report why it does not.
        Assert.Equal(0, after.NonQuadCount);
        Assert.Equal(after.QuadCount, renumbered.FaceCount);
        Assert.True(after.QuadCount > 0 || after.Warnings.Count > 0, $"{shape} refused without saying so.");
        Assert.NotEmpty(after.Stages);

        if (invariant) {
            SameBits(reference, renumbered, $"{shape}, renumbered");
            SameNumbers(before, after, $"{shape}, renumbered");
        }
    }

    /// <summary>The fixture has to be worth comparing before a comparison over it means anything.</summary>
    static void Guard(RemeshReport report, EditMesh mesh) {
        Assert.True(report.QuadCount > 200, $"{report.QuadCount} quads: {string.Join(" · ", report.Warnings)}");
        Assert.Equal(report.QuadCount, mesh.FaceCount);
        Assert.True(report.Conditioning.Triangles > 800, $"{report.Conditioning.Triangles} triangles will not split.");

        Assert.True(
            report.Singularities.Count >= 8,
            "A field with no structure in it produces identical output whatever the order."
        );
    }

    static void SameBits(EditMesh expected, EditMesh actual, string what) {
        Assert.Equal(expected.PositionCount, actual.PositionCount);
        Assert.Equal(expected.FaceCount, actual.FaceCount);
        Assert.Equal(expected.CornerCount, actual.CornerCount);

        for (var position = 0; position < expected.PositionCount; position++) {
            var want = expected.Positions[position];
            var got = actual.Positions[position];

            Assert.True(
                BitConverter.SingleToInt32Bits(want.X) == BitConverter.SingleToInt32Bits(got.X)
                && BitConverter.SingleToInt32Bits(want.Y) == BitConverter.SingleToInt32Bits(got.Y)
                && BitConverter.SingleToInt32Bits(want.Z) == BitConverter.SingleToInt32Bits(got.Z),
                $"{what}: position {position} is {got} against {want}. docs/plan/41 § D14 asks for the same bits."
            );
        }

        for (var corner = 0; corner < expected.CornerCount; corner++) {
            Assert.Equal(expected.Corners[corner], actual.Corners[corner]);
        }
    }

    /// <summary>Every measured field of the report, bit for bit — a timing is not a measurement.</summary>
    /// <remarks>
    ///     ⚠ <b>The mesh matching is not enough on its own.</b> Half of docs/plan/41 § Part 4 is
    ///     computed from artefacts the output mesh does not carry — the singularity list walks the
    ///     extraction, the feature error walks the layout, the conditioning counts belong to a stage
    ///     whose result was thrown away — so a gate that compared only positions and corners would
    ///     leave every one of them unswept.
    /// </remarks>
    static void SameNumbers(RemeshReport expected, RemeshReport actual, string what) {
        Assert.Equal(expected.QuadCount, actual.QuadCount);
        Assert.Equal(expected.NonQuadCount, actual.NonQuadCount);
        Assert.Equal(expected.Singularities, actual.Singularities);
        Assert.Equal(expected.SingularitiesOnFeatures, actual.SingularitiesOnFeatures);
        Assert.Equal(expected.Warnings, actual.Warnings);

        SameConditioning(expected.Conditioning, actual.Conditioning);
        SameMesh(expected.Mesh, actual.Mesh);

        Same(expected.MaxDeviation, actual.MaxDeviation, nameof(RemeshReport.MaxDeviation), what);
        Same(expected.MeanDeviation, actual.MeanDeviation, nameof(RemeshReport.MeanDeviation), what);
        Same(expected.MinScaledJacobian, actual.MinScaledJacobian, nameof(RemeshReport.MinScaledJacobian), what);

        Same(
            expected.FeatureReproductionError,
            actual.FeatureReproductionError,
            nameof(RemeshReport.FeatureReproductionError),
            what
        );

        // A stage's elapsed time is a clock reading and is deliberately not compared; what it handled
        // is a measurement and is.
        Assert.Equal(expected.Stages.Count, actual.Stages.Count);

        for (var stage = 0; stage < expected.Stages.Count; stage++) {
            Assert.Equal(expected.Stages[stage].Stage, actual.Stages[stage].Stage);
            Assert.Equal(expected.Stages[stage].Elements, actual.Stages[stage].Elements);
        }
    }

    /// <summary>Every conditioning count, and the surface report under it element by element.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="ConditioningReport" /> cannot be compared with <c>Assert.Equal</c> and the
    ///     reason is a trap rather than a preference.</b> It is a record struct whose generated equality
    ///     is memberwise, and one of its members is a <see cref="MeshReport" /> whose four members are
    ///     <see cref="IReadOnlyList{T}" /> — so the comparison lands on <c>List&lt;int&gt;</c>'s
    ///     <i>reference</i> equality and two identical reports from two runs are never equal. It fails
    ///     printing two lines that read as the same value, which is the worst way to spend an hour.
    /// </remarks>
    static void SameConditioning(ConditioningReport expected, ConditioningReport actual) {
        Assert.Equal(expected.Welded, actual.Welded);
        Assert.Equal(expected.Reoriented, actual.Reoriented);
        Assert.Equal(expected.Unorientable, actual.Unorientable);
        Assert.Equal(expected.Despecked, actual.Despecked);
        Assert.Equal(expected.Cut, actual.Cut);
        Assert.Equal(expected.Filled, actual.Filled);
        Assert.Equal(expected.Shrinkwrapped, actual.Shrinkwrapped);
        Assert.Equal(expected.Triangles, actual.Triangles);

        SameMesh(expected.Mesh, actual.Mesh);
    }

    static void SameMesh(MeshReport expected, MeshReport actual) {
        Assert.Equal(expected.NonManifold, actual.NonManifold);
        Assert.Equal(expected.Boundary, actual.Boundary);
        Assert.Equal(expected.Reversed, actual.Reversed);
        Assert.Equal(expected.Degenerate, actual.Degenerate);
        Assert.Equal(expected.Orphans, actual.Orphans);
    }

    static void Same(float expected, float actual, string field, string what) =>
        Assert.True(
            BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            $"{what}: {field} is {actual:E6} against {expected:E6}. Close is not the contract."
        );

    /// <summary>Rebuilds a mesh with its positions renumbered by a fixed permutation.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixed sequence rather than a random one</b>, for docs/plan/41 § B3's reason: a test
    ///     that measures against a moving permutation cannot tell a regression from a reseed. This is
    ///     the xorshift <c>ShapeCorpus.Renumber</c> in <c>Vixen.Geometry.Uv.Tests</c> uses, kept here
    ///     rather than shared because the two assemblies share only <c>RunawayGuard</c> and one helper
    ///     is not a reason to link a second file.
    /// </remarks>
    internal static EditMesh Renumber(EditMesh mesh, uint seed) {
        var order = new int[mesh.PositionCount];

        for (var index = 0; index < order.Length; index++) {
            order[index] = index;
        }

        var state = seed;

        for (var index = order.Length - 1; index > 0; index--) {
            var swap = (int)(Next(ref state) % (uint)(index + 1));

            (order[index], order[swap]) = (order[swap], order[index]);
        }

        var moved = new EditMesh();
        var slot = new int[mesh.PositionCount];

        for (var index = 0; index < order.Length; index++) {
            slot[order[index]] = moved.AddPosition(mesh.Positions[order[index]]);
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);
            var mapped = new int[loop.Length];

            for (var corner = 0; corner < loop.Length; corner++) {
                mapped[corner] = slot[loop[corner]];
            }

            moved.AddFace(mapped);
        }

        return moved;
    }

    static uint Next(ref uint state) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        return state;
    }
}
