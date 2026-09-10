// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The same islands pack the same way — in a different order, and on any number of workers.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § B6, § D7 and § D12. The content hash has to be a function of the input, and
///         a golden must not move. That excludes simulated annealing, genetic search and random
///         restarts, which are the irregular-packing literature's three standard answers, and it
///         leaves an ordering by descending area with an explicit index tie-break.
///     </para>
///     <para>
///         ⚠ <b>Two axes, not one.</b> The worker count is the obvious one. The batch size is the
///         second and it is independent: four workers handed the same work in pieces of a different
///         size visit it in a different order, and a gate that only swept workers would not have
///         covered it. <c>Vixen.Vfx.Tests/VfxParallelTests</c> is the pattern, down to the guard that
///         asserts the fixture is non-trivial before the comparison means anything.
///     </para>
///     <para>
///         ⚠ <b>Every scheduler is disposed.</b> <see cref="JobScheduler.MaxSchedulers" /> is a
///         process-wide cap of eight that frees only on <c>Dispose</c>, and xunit runs test classes in
///         parallel — a scheduler left to the finalizer is a cap the next class trips over, with a
///         failure that names the wrong test.
///     </para>
/// </remarks>
public class UvPackDeterminismTests {
    static PackSettings Settings => new() { Resolution = 512, Margin = 4, CoreLimit = 64 };

    /// <summary>docs/plan/42 § D7: shuffled in, and the same set of placements out.</summary>
    [Fact]
    public void TheSameIslandsInADifferentOrderPackIdentically() {
        var islands = IslandCorpus.Trellis(180);
        var straight = UvUnwrap.Pack(islands, Settings);
        var (shuffled, origin) = IslandCorpus.Shuffle(islands, 0x1234u);

        Assert.NotEqual(Enumerable.Range(0, islands.Length), origin);

        var permuted = UvUnwrap.Pack(shuffled, Settings);

        for (var index = 0; index < islands.Length; index++) {
            var expected = straight[origin[index]];
            var actual = permuted[index];

            Assert.Equal(expected.Offset, actual.Offset);
            Assert.Equal(expected.Scale, actual.Scale);
            Assert.Equal(expected.Rotation, actual.Rotation);
            Assert.Equal(expected.Tile, actual.Tile);
        }
    }

    [Fact]
    public void TenRunsOnOneThreadAreTheSameRun() {
        var islands = IslandCorpus.Trellis(150);
        var first = UvUnwrap.Pack(islands, Settings);

        for (var run = 0; run < 9; run++) {
            Assert.Equal(first, UvUnwrap.Pack(islands, Settings));
        }
    }

    /// <summary>None, one, four and sixteen workers, and the placements are byte-identical.</summary>
    /// <param name="workers">How many worker threads the scheduler owns, or nought for the browser.</param>
    /// <remarks>
    ///     ⚠ <b>The nought row is a different claim from the others and not a cheaper one.</b> A
    ///     scheduler with no workers runs a batch when a thread reaches it rather than on a worker, so
    ///     work that is scheduled and never waited on does nothing at all there while being invisible
    ///     at four — see <c>Core/Vixen.Core.Threading/README.md</c> and #328. It is the count
    ///     <c>browser-wasm</c> picks by construction, because <c>Thread.Start</c> throws there.
    ///     <c>Packer.cs</c>'s <c>if (scheduler is not null &amp;&amp; islands.Count > 1)</c> is what
    ///     decides the scheduled path, so this row takes it rather than falling back into the serial
    ///     branch the comparison is against.
    /// </remarks>
    [Trait("Workers", "0")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void EveryWorkerCountGivesTheSamePlacements(int workers) {
        var islands = IslandCorpus.Trellis(180);
        var serial = UvUnwrap.Pack(islands, Settings);

        Assert.True(
            serial.Select(placement => placement.Offset).Distinct().Count() > 100,
            "The fixture packed everything into a handful of spots, so agreeing about it proves nothing."
        );

        Assert.True(
            serial.Any(placement => placement.Rotation != 0),
            "Nothing was turned, so the orientation half of the scan is untested here."
        );

        using var scheduler = new JobScheduler(workers);
        var parallel = UvUnwrap.Pack(islands, Settings, scheduler, out _);

        Assert.Equal(serial, parallel);
    }

    /// <summary>⚠ The second axis: the same workers, different batch sizes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(64)]
    public void EveryBatchSizeGivesTheSamePlacements(int batch) {
        var islands = IslandCorpus.Trellis(180);
        var serial = UvUnwrap.Pack(islands, Settings);

        using var scheduler = new JobScheduler(4);
        var batched = UvUnwrap.Pack(islands, Settings, scheduler, batch, out _);

        Assert.Equal(serial, batched);
    }

    [Fact]
    public void TheReportedEfficiencyIsTheSameAcrossWorkerCounts() {
        var islands = IslandCorpus.Trellis(180);

        UvUnwrap.Pack(islands, Settings, null, out var serial);

        using var one = new JobScheduler(1);
        using var many = new JobScheduler(8);

        UvUnwrap.Pack(islands, Settings, one, out var single);
        UvUnwrap.Pack(islands, Settings, many, out var multiple);

        Assert.Equal(serial.PackingEfficiency, single.PackingEfficiency);
        Assert.Equal(serial.PackingEfficiency, multiple.PackingEfficiency);
        Assert.Equal(serial.EffectiveEfficiency, multiple.EffectiveEfficiency);
    }
}
