// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>docs/plan/42 § U5's gate, over the fused verb and over the numbers it reports.</summary>
/// <remarks>
///     <para>
///         <b>What this adds to <c>UvChartDeterminismTests</c> and <c>UvPackDeterminismTests</c>, which
///         already sweep, is the three things a per-stage gate cannot reach.</b> § D12 asks for
///         byte-identical output at any thread count; the existing classes run each parallel leg
///         <i>once</i> and compare the <i>coordinates</i> on <i>one</i> shape. So: ten runs at each
///         worker count rather than one, every worker count against every other rather than each
///         against the serial leg, and every measured field of <see cref="UvReport" /> compared bit for
///         bit rather than only <see cref="UvReport.ChartCount" />.
///     </para>
///     <para>
///         ⚠ <b>A report field that is not in the comparison is a field a thread can move.</b> Half of
///         § Part 4 is assembled in <c>UvUnwrap.Combine</c> from three stages' reports, and the
///         distortion measures, the efficiency pair and the achieved density are each summed over a
///         collection the scheduler handed out. Comparing the coordinates and stopping leaves all of
///         them unswept, which is the same gap docs/plan/41 § Part 4 exists to close on the other side.
///     </para>
///     <para>
///         ⚠ <b><see cref="PackSettings.Resolution" /> is pinned and small on purpose.</b> The whole
///         sweep is a few hundred unwraps; a 2048² atlas rasterizes islands into sixteen times the
///         texels for a claim that has nothing to do with how many there are.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <see cref="JobScheduler.MaxSchedulers" /> is a
///         process-wide cap of eight that frees only on <c>Dispose</c>, and xunit runs test classes in
///         parallel — a scheduler left to the finalizer fails a test somewhere else, later, in a class
///         that has nothing to do with this one.
///     </para>
/// </remarks>
public class UvUnwrapDeterminismTests {
    /// <summary>How many times each configuration is re-run. § U5 and doc 41 § R4 both ask for ten.</summary>
    const int Runs = 10;

    static PackSettings Packing => new() { Resolution = 512, Margin = 4, TexelDensity = 64f };

    /// <summary>The two axes, crossed — and the batch sizes reach past the work as well as under it.</summary>
    /// <remarks>
    ///     ⚠ <b>A batch larger than the whole job is its own case.</b> One work item covering every
    ///     chart runs the entire loop on one worker whatever the worker count says, which is the
    ///     configuration that silently stops testing the thing under test — so it is swept alongside a
    ///     batch of one, which is the opposite extreme, and zero, which is the scheduler's own choice.
    /// </remarks>
    public static TheoryData<int, int> Configurations {
        get {
            var data = new TheoryData<int, int>();

            foreach (var workers in new[] { 1, 4, 16 }) {
                foreach (var batch in new[] { 0, 1, 7, 512 }) {
                    data.Add(workers, batch);
                }
            }

            return data;
        }
    }

    /// <summary>Ten unwraps at one worker count and one batch size are one unwrap.</summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void TenRunsAtOneConfigurationAreOneRun(int workers, int batch) {
        // ⚠ Coarser than the corpus default, and it is the fixture rather than the claim that shrank.
        // A hundred and twenty unwraps pay for tessellation linearly and gain nothing from it; what the
        // guard below asks of the mesh — several charts, and enough corners to split across sixteen
        // workers — a sixteen-by-twenty lathe satisfies as well as a thirty-two-by-forty one.
        var mesh = ShapeCorpus.Dumbbell(1f, 16, 20);

        using var scheduler = new JobScheduler(workers);

        Assert.Equal(workers, scheduler.WorkerCount);

        var first = UvUnwrap.All(mesh, new(), Packing, scheduler, batch, out var coordinates);

        Guard(first, coordinates, mesh);

        for (var run = 1; run < Runs; run++) {
            var again = UvUnwrap.All(mesh, new(), Packing, scheduler, batch, out var repeat);

            SameBits(coordinates, repeat, $"{workers} workers, batch {batch}, run {run}");
            SameReport(first, again, $"{workers} workers, batch {batch}, run {run}");
        }
    }

    /// <summary>Every worker count agrees with every other, over the whole three-stage call.</summary>
    /// <remarks>
    ///     ⚠ <b>Each against the serial leg is a weaker claim than each against each.</b> Two parallel
    ///     legs can agree with the calling thread on the coordinates and disagree with one another on a
    ///     figure the serial leg reached by another route; the comparison that catches that is the
    ///     transitive one. The schedulers are built and disposed one at a time, so the cap of eight is
    ///     never approached however many neighbouring classes are running.
    /// </remarks>
    [Theory]
    [InlineData("dumbbell")]
    [InlineData("sphere-cut-open")]
    [InlineData("torus-closed")]
    public void EveryWorkerCountAgreesWithEveryOther(string shape) {
        var mesh = Fixture(shape);
        var answers = new List<(UvReport Report, IReadOnlyList<Vector2> Uvs, string What)>();

        foreach (var workers in new[] { 1, 2, 4, 8, 16 }) {
            using var scheduler = new JobScheduler(workers);

            answers.Add((UvUnwrap.All(mesh, new(), Packing, scheduler, 0, out var uvs), uvs, $"{workers} workers"));
        }

        Guard(answers[0].Report, answers[0].Uvs, mesh);

        foreach (var answer in answers) {
            SameBits(answers[0].Uvs, answer.Uvs, $"{shape}, {answer.What}");
            SameReport(answers[0].Report, answer.Report, $"{shape}, {answer.What}");
        }
    }

    /// <summary>Ten unwraps on the calling thread are one unwrap, on three shapes that fail differently.</summary>
    /// <remarks>
    ///     The half of the gate that needs no thread: a pipeline reading a <c>Dictionary</c>'s order, a
    ///     clock or a hash seed anywhere fails this without a scheduler ever being constructed. ⚠ A
    ///     greedy pass that iterates a <see cref="HashSet{T}" /> is reproducible on one runtime and not
    ///     on the next, so "it passed here" is not the claim — the claim is that every ordering decision
    ///     in the three stages is over a sorted sequence with an explicit tie-break.
    /// </remarks>
    [Theory]
    [InlineData("dumbbell")]
    [InlineData("sphere-cut-open")]
    [InlineData("torus-closed")]
    public void TenRunsOnTheCallingThreadAreOneRun(string shape) {
        var mesh = Fixture(shape);
        var first = UvUnwrap.All(mesh, new(), Packing, out var coordinates);

        Guard(first, coordinates, mesh);

        for (var run = 1; run < Runs; run++) {
            var again = UvUnwrap.All(mesh, new(), Packing, out var repeat);

            SameBits(coordinates, repeat, $"{shape}, run {run}");
            SameReport(first, again, $"{shape}, run {run}");
        }
    }

    /// <summary>Renumbering the input's positions, which no thread sweep can catch.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A vertex numbering is not a property of a surface, and this is the sweep that says
    ///         so.</b> An importer, a weld and a boolean renumber a mesh routinely. § D5's pins are
    ///         chosen by <i>position</i> rather than by index for exactly this reason, and the charter's
    ///         seeds and merge order are sorted rather than enumerated — so the atlas has to survive a
    ///         permutation of the input that changes nothing about the shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A tolerance rather than a bit comparison, and the reason is arithmetic rather than
    ///         a hedge.</b> The renumbered mesh assembles the same linear system with its rows
    ///         permuted, and the same sums added in another order differ in the last bits. What must
    ///         not move is the <i>atlas</i>: the same charts, the same seam length to a part in a
    ///         thousand, and every coordinate within a thousandth of the unit square.
    ///     </para>
    ///     <para>
    ///         ⚠ Renumbering permutes <i>positions</i> and leaves the faces where they are, so the
    ///         corner order is preserved and the two coordinate lists compare one for one.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("dumbbell")]
    [InlineData("sphere-cut-open")]
    [InlineData("torus-closed")]
    public void ARenumberedInputUnwrapsToTheSameAtlas(string shape) {
        var mesh = Fixture(shape);
        var moved = ShapeCorpus.Renumber(mesh, 0x51F3u);

        Assert.NotEqual(
            Enumerable.Range(0, mesh.PositionCount).Select(index => mesh.Positions[index]).ToArray(),
            Enumerable.Range(0, moved.PositionCount).Select(index => moved.Positions[index]).ToArray()
        );

        var before = UvUnwrap.All(mesh, new(), Packing, out var first);
        var after = UvUnwrap.All(moved, new(), Packing, out var second);

        Guard(before, first, mesh);

        Assert.Equal(before.ChartCount, after.ChartCount);
        Assert.Equal(before.Distortion.Flipped, after.Distortion.Flipped);
        Assert.Equal(first.Count, second.Count);

        Assert.True(
            MathF.Abs(before.SeamLength - after.SeamLength) <= 1e-3f * MathF.Max(1f, before.SeamLength),
            $"{shape}: the seam went from {before.SeamLength:F5} to {after.SeamLength:F5} on a renumbering."
        );

        for (var corner = 0; corner < first.Count; corner++) {
            Assert.True(
                Vector2.Distance(first[corner], second[corner]) < 1e-3f,
                $"{shape}: corner {corner} moved from {first[corner]} to {second[corner]} on a renumbering."
            );
        }
    }

    /// <summary>The fixture has to be worth comparing before a comparison over it means anything.</summary>
    /// <remarks>
    ///     ⚠ <c>VfxParallelTests</c>' rule. A mesh that charts to one region runs on the calling thread
    ///     at every worker count and a refused chart leaves its corners at zero — either way two
    ///     identical answers would prove nothing at all.
    /// </remarks>
    static void Guard(UvReport report, IReadOnlyList<Vector2> uvs, EditMesh mesh) {
        Assert.True(report.ChartCount > 2, $"{report.ChartCount} charts is not a fixture.");
        Assert.Equal(mesh.CornerCount, uvs.Count);
        Assert.True(report.IsInjective, $"the fixture folded: {string.Join(" · ", report.Warnings)}");
        Assert.True(uvs.Count > 1000, $"{uvs.Count} corners will not split across sixteen workers.");

        Assert.True(
            report.PackingEfficiency > 0f && report.TexelDensity.Mean > 0f,
            "The pack stage did nothing, so its half of the report is zero whatever the thread count."
        );
    }

    static void SameBits(IReadOnlyList<Vector2> expected, IReadOnlyList<Vector2> actual, string what) {
        Assert.Equal(expected.Count, actual.Count);

        for (var corner = 0; corner < expected.Count; corner++) {
            Assert.True(
                BitConverter.SingleToInt32Bits(expected[corner].X)
                == BitConverter.SingleToInt32Bits(actual[corner].X)
                && BitConverter.SingleToInt32Bits(expected[corner].Y)
                == BitConverter.SingleToInt32Bits(actual[corner].Y),
                $"{what}: corner {corner} is {actual[corner]} against {expected[corner]}. "
                + "docs/plan/42 § D12 asks for the same bits."
            );
        }
    }

    /// <summary>Every measured field of docs/plan/42 § Part 4, bit for bit — a timing is not one.</summary>
    static void SameReport(UvReport expected, UvReport actual, string what) {
        Assert.Equal(expected.ChartCount, actual.ChartCount);
        Assert.Equal(expected.Distortion.Flipped, actual.Distortion.Flipped);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Stages.Count, actual.Stages.Count);

        for (var stage = 0; stage < expected.Stages.Count; stage++) {
            Assert.Equal(expected.Stages[stage].Stage, actual.Stages[stage].Stage);
            Assert.Equal(expected.Stages[stage].Elements, actual.Stages[stage].Elements);
        }

        Same(expected.Compactness, actual.Compactness, nameof(UvReport.Compactness), what);
        Same(expected.Convexity, actual.Convexity, nameof(UvReport.Convexity), what);
        Same(expected.SeamLength, actual.SeamLength, nameof(UvReport.SeamLength), what);
        Same(expected.BoundaryJaggedness, actual.BoundaryJaggedness, nameof(UvReport.BoundaryJaggedness), what);
        Same(expected.PackingEfficiency, actual.PackingEfficiency, nameof(UvReport.PackingEfficiency), what);
        Same(expected.EffectiveEfficiency, actual.EffectiveEfficiency, nameof(UvReport.EffectiveEfficiency), what);
        Same(expected.Distortion.Angular, actual.Distortion.Angular, "Distortion.Angular", what);
        Same(expected.Distortion.Area, actual.Distortion.Area, "Distortion.Area", what);
        Same(expected.Distortion.StretchL2, actual.Distortion.StretchL2, "Distortion.StretchL2", what);
        Same(expected.Distortion.StretchLInf, actual.Distortion.StretchLInf, "Distortion.StretchLInf", what);
        Same(expected.TexelDensity.Minimum, actual.TexelDensity.Minimum, "TexelDensity.Minimum", what);
        Same(expected.TexelDensity.Mean, actual.TexelDensity.Mean, "TexelDensity.Mean", what);
        Same(expected.TexelDensity.Maximum, actual.TexelDensity.Maximum, "TexelDensity.Maximum", what);
        Same(expected.TexelDensity.Variance, actual.TexelDensity.Variance, "TexelDensity.Variance", what);

        Same(
            expected.SeamLengthNormalized,
            actual.SeamLengthNormalized,
            nameof(UvReport.SeamLengthNormalized),
            what
        );
    }

    static void Same(float expected, float actual, string field, string what) =>
        Assert.True(
            BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
            $"{what}: {field} is {actual:E6} against {expected:E6}. Close is not the contract."
        );

    static EditMesh Fixture(string shape) => shape switch {
        "dumbbell" => ShapeCorpus.Dumbbell(),
        "sphere-cut-open" => ShapeCorpus.SphereCutOpen(),
        "torus-closed" => ShapeCorpus.TorusClosed(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No such fixture.")
    };
}
