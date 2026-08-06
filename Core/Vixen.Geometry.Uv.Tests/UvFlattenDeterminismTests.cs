// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Byte-identical coordinates, at any worker count and any batch size.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D12 and doc 41 § D14. <b>Not close — the same bits</b>, which is what
///         <see cref="BitConverter.SingleToInt32Bits" /> below is for: a comparison with a tolerance
///         would pass on a flattener that had a nondeterministic reduction in it and was merely
///         converging to the same place.
///     </para>
///     <para>
///         ⚠ <b>Charts are the unit of parallelism and that is the whole design.</b> Nothing inside one
///         chart's solve is threaded — every dot product sums in index order on one thread — so there
///         is no reduction whose order a worker count could change. What the sweep actually proves is
///         that no chart reads another's state, which is the failure a shared solver instance would
///         cause and which the type's "not thread-safe" remark exists for.
///     </para>
///     <para>
///         ⚠ <b>Worker count and batch size are two axes and testing one is not testing the other.</b>
///         Four workers handed the same charts in different-sized pieces visit them in a different
///         order, and a gate that only swept worker counts would not have covered it.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <see cref="JobScheduler.MaxSchedulers" /> is a
///         process-wide cap of eight that frees only on <c>Dispose</c>, and xunit runs test classes in
///         parallel — a scheduler left to the finalizer fails a test somewhere else, later, in a class
///         that has nothing to do with this one.
///     </para>
/// </remarks>
public class UvFlattenDeterminismTests {
    /// <summary>Enough charts that sixteen workers have something to disagree about.</summary>
    const int Charts = 48;

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
    public void AFlattenIsTheSameBitsAtAnyWorkerCountAndBatchSize(int workers, int batch) {
        var mesh = ShapeCorpus.SphereCutOpen();
        var charts = ShapeCorpus.Strips(mesh, Charts);
        var serial = UvUnwrap.Flatten(mesh, charts, new(), out var serialReport);

        // ⚠ Non-trivial before the comparison means anything, which is `VfxParallelTests`' guard. A
        // fixture of one chart runs on the calling thread at every worker count, so the test would
        // pass by never having tested anything.
        Assert.Equal(Charts, serial.Count);
        Assert.True(serial.Sum(island => island.TriangleCount) > 1000, "too few triangles to be a fixture");

        using var scheduler = new JobScheduler(workers);
        Assert.Equal(workers, scheduler.WorkerCount);

        var parallel = UvUnwrap.Flatten(mesh, charts, new(), scheduler, batch, out var parallelReport);

        Assert.Equal(serialReport.Distortion, parallelReport.Distortion);
        Assert.Equal(serialReport.Warnings, parallelReport.Warnings);
        AssertSameBits(serial, parallel);
    }

    /// <summary>Three worker counts against each other rather than each against the serial leg.</summary>
    [Fact]
    public void EveryWorkerCountAgreesWithEveryOther() {
        var mesh = ShapeCorpus.Hemisphere();
        var charts = ShapeCorpus.Strips(mesh, 24);
        var answers = new List<IReadOnlyList<UvIsland>>();

        // ⚠ Sequential, and disposed each time round. Sixteen schedulers alive at once would be twice
        // the process-wide cap, and the exception it throws names the cap rather than this test.
        foreach (var workers in new[] { 1, 2, 4, 8, 16 }) {
            using var scheduler = new JobScheduler(workers);

            answers.Add(UvUnwrap.Flatten(mesh, charts, new(), scheduler, 0, out _));
        }

        foreach (var answer in answers) {
            AssertSameBits(answers[0], answer);
        }
    }

    /// <summary>Ten runs on one thread are one run.</summary>
    [Fact]
    public void TenRunsOnOneThreadAreTheSameRun() {
        var mesh = ShapeCorpus.TorusSlit();
        var charts = ShapeCorpus.Strips(mesh, 16);
        var first = UvUnwrap.Flatten(mesh, charts, new());

        for (var run = 0; run < 9; run++) {
            AssertSameBits(first, UvUnwrap.Flatten(mesh, charts, new()));
        }
    }

    /// <summary>A refused chart is refused the same way on every thread count.</summary>
    [Fact]
    public void ARefusalIsDeterministicToo() {
        var mesh = ShapeCorpus.CylinderClosed();
        var charts = ShapeCorpus.OneChart(mesh);
        var serial = UvUnwrap.Detail(mesh, charts, new(), null, 0);

        using var scheduler = new JobScheduler(4);

        var parallel = UvUnwrap.Detail(mesh, charts, new(), scheduler, 3);

        Assert.Equal(serial.Refused, parallel.Refused);
        Assert.Equal(serial.Report.Warnings, parallel.Report.Warnings);
    }

    static void AssertSameBits(IReadOnlyList<UvIsland> expected, IReadOnlyList<UvIsland> actual) {
        Assert.Equal(expected.Count, actual.Count);

        for (var island = 0; island < expected.Count; island++) {
            Assert.Equal(expected[island].Corners, actual[island].Corners);
            Assert.Equal(expected[island].Coordinates.Count, actual[island].Coordinates.Count);

            for (var corner = 0; corner < expected[island].Coordinates.Count; corner++) {
                var want = expected[island].Coordinates[corner];
                var got = actual[island].Coordinates[corner];

                Assert.True(
                    BitConverter.SingleToInt32Bits(want.X) == BitConverter.SingleToInt32Bits(got.X)
                    && BitConverter.SingleToInt32Bits(want.Y) == BitConverter.SingleToInt32Bits(got.Y),
                    $"Island {island} corner {corner} is {got} against {want}. Close is not the contract; "
                    + "docs/plan/42 § D12 asks for the same bits."
                );
            }

            Assert.Equal(
                BitConverter.SingleToInt32Bits(expected[island].Scale),
                BitConverter.SingleToInt32Bits(actual[island].Scale)
            );
        }
    }
}
