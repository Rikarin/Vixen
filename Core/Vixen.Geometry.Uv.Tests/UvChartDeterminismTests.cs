// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The same seams and the same atlas, at any worker count and any batch size.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D12 and doc 41 § D14. ⚠ <b>The failure this is really aimed at is not a
///         thread, it is a <c>Dictionary</c>.</b> A greedy merge that iterates a hash set merges in a
///         different order on a different runtime, and every ordering decision in the charter — the
///         seeds, the candidate splits, the merge pairs — is a place where enumerating an unordered
///         collection would produce a perfectly reproducible answer on this machine and a different one
///         on somebody else's.
///     </para>
///     <para>
///         ⚠ <b>Charting adds no parallelism of its own, which is the reason the sweep is cheap and the
///         reason it is still worth running.</b> A whole level of the recursion is handed to the
///         flattener as one chart assignment, so the threaded work is U2's and its determinism is
///         already gated — what this proves is that no <i>decision</i> the charter makes reads anything
///         the scheduler touched.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <see cref="JobScheduler.MaxSchedulers" /> is a
///         process-wide cap of eight that frees only on <c>Dispose</c>, and xunit runs test classes in
///         parallel — a scheduler left to the finalizer fails a test somewhere else, later, in a class
///         that has nothing to do with this one.
///     </para>
/// </remarks>
public class UvChartDeterminismTests {
    public static TheoryData<int, int> Configurations {
        get {
            var data = new TheoryData<int, int>();

            foreach (var workers in new[] { 1, 4, 16 }) {
                foreach (var batch in new[] { 0, 1, 7 }) {
                    data.Add(workers, batch);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void TheSeamsAreTheSameAtAnyWorkerCountAndBatchSize(int workers, int batch) {
        var mesh = ShapeCorpus.Dumbbell();
        var serial = UvUnwrap.Charts(mesh, new(), null, 0, true, out var serialReport);

        // ⚠ Non-trivial before the comparison means anything. A mesh that charts to one region runs on
        // the calling thread at every worker count, so the test would pass by never having tested it.
        Assert.True(serialReport.ChartCount > 2, "too few charts to be a fixture");

        using var scheduler = new JobScheduler(workers);

        Assert.Equal(workers, scheduler.WorkerCount);

        var parallel = UvUnwrap.Charts(mesh, new(), scheduler, batch, true, out var parallelReport);

        Assert.Equal(serial, parallel);
        Assert.Equal(serialReport.ChartCount, parallelReport.ChartCount);
        Assert.Equal(serialReport.Warnings, parallelReport.Warnings);

        Assert.True(
            BitConverter.SingleToInt32Bits(serialReport.SeamLength)
            == BitConverter.SingleToInt32Bits(parallelReport.SeamLength),
            $"the seam went from {serialReport.SeamLength} to {parallelReport.SeamLength} at {workers} "
            + $"workers and a batch of {batch}."
        );
    }

    /// <summary>And the whole three-stage call is byte-identical, coordinate for coordinate.</summary>
    /// <remarks>
    ///     ⚠ <b>Not close — the same bits.</b> A comparison with a tolerance would pass on a pipeline
    ///     that had a nondeterministic reduction in it and was merely converging to the same place, and
    ///     docs/plan/42 § B6's reason for the gate is a content hash, which does not have a tolerance.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void TheWholeUnwrapIsTheSameBits(int workers, int batch) {
        var mesh = ShapeCorpus.Dumbbell();
        var packing = new PackSettings { Resolution = 512, Margin = 2 };
        var serialReport = UvUnwrap.All(mesh, new(), packing, null, 0, out var serial);

        Assert.True(serialReport.ChartCount > 2, "too few charts to be a fixture");
        Assert.Equal(mesh.CornerCount, serial.Count);

        using var scheduler = new JobScheduler(workers);

        var parallelReport = UvUnwrap.All(mesh, new(), packing, scheduler, batch, out var parallel);

        Assert.Equal(serialReport.ChartCount, parallelReport.ChartCount);
        Assert.Equal(serial.Count, parallel.Count);

        for (var corner = 0; corner < serial.Count; corner++) {
            Assert.True(
                BitConverter.SingleToInt32Bits(serial[corner].X) == BitConverter.SingleToInt32Bits(parallel[corner].X)
                && BitConverter.SingleToInt32Bits(serial[corner].Y)
                == BitConverter.SingleToInt32Bits(parallel[corner].Y),
                $"Corner {corner} is {parallel[corner]} against {serial[corner]} at {workers} workers and a "
                + $"batch of {batch}. docs/plan/42 § D12 asks for the same bits."
            );
        }
    }

    /// <summary>Every worker count agrees with every other, rather than each with the serial leg.</summary>
    [Fact]
    public void EveryWorkerCountAgreesWithEveryOther() {
        var mesh = ShapeCorpus.TorusClosed();
        var answers = new List<IReadOnlyList<int>>();

        // ⚠ Sequential, and disposed each time round. Five schedulers alive at once approaches the
        // process-wide cap, and the exception it throws names the cap rather than this test.
        foreach (var workers in new[] { 1, 2, 4, 8, 16 }) {
            using var scheduler = new JobScheduler(workers);

            answers.Add(UvUnwrap.Charts(mesh, new(), scheduler, 0, true, out _));
        }

        foreach (var answer in answers) {
            Assert.Equal(answers[0], answer);
        }
    }

    /// <summary>Ten runs on one thread are one run.</summary>
    [Fact]
    public void TenRunsOnOneThreadAreTheSameRun() {
        var mesh = ShapeCorpus.Dumbbell();
        var packing = new PackSettings { Resolution = 512, Margin = 2 };

        UvUnwrap.All(mesh, new(), packing, out var first);

        for (var run = 0; run < 9; run++) {
            UvUnwrap.All(mesh, new(), packing, out var again);

            Assert.Equal(first.Count, again.Count);

            for (var corner = 0; corner < first.Count; corner++) {
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(first[corner].X),
                    BitConverter.SingleToInt32Bits(again[corner].X)
                );

                Assert.Equal(
                    BitConverter.SingleToInt32Bits(first[corner].Y),
                    BitConverter.SingleToInt32Bits(again[corner].Y)
                );
            }
        }
    }

    /// <summary>And a renumbered mesh unwraps to the same atlas, which a thread sweep cannot catch.</summary>
    [Fact]
    public void ARenumberedMeshUnwrapsToTheSameAtlas() {
        var mesh = ShapeCorpus.SphereCutOpen();
        var moved = ShapeCorpus.Renumber(mesh, 0x9E37u);
        var packing = new PackSettings { Resolution = 512, Margin = 2 };

        var before = UvUnwrap.All(mesh, new(), packing, out var first);
        var after = UvUnwrap.All(moved, new(), packing, out var second);

        Assert.Equal(before.ChartCount, after.ChartCount);
        Assert.Equal(first.Count, second.Count);

        // Corner order is preserved by the renumbering — it permutes positions, not faces — so the
        // coordinates are comparable one for one. What is left is the summation order inside the solve,
        // which is why this is a tolerance rather than a bit comparison.
        for (var corner = 0; corner < first.Count; corner++) {
            Assert.True(
                Vector2.Distance(first[corner], second[corner]) < 1e-3f,
                $"Corner {corner} moved from {first[corner]} to {second[corner]} on a renumbering."
            );
        }
    }
}
