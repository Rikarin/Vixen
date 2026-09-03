// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     The policy half of thread affinity: which processor a worker is handed, and what happens on
///     the platforms that have none to hand out.
/// </summary>
/// <remarks>
///     ⚠ <b>The fake below is not more permissive than the real thing.</b> It refuses a processor
///     index outside <c>[0, AvailableProcessors)</c> by recording it and letting the assertion see
///     it, rather than clamping — a topology that silently accepted an out-of-range index would make
///     the wrap-around case below pass whether or not the wrap exists, which is the whole of what
///     that case is for.
/// </remarks>
public class ProcessorAffinityPlacementTests {
    [Fact]
    public void PerformanceCoresAreHandedOutFirst() {
        // Four efficiency cores in front of four performance ones, so an implementation that simply
        // counted from zero would look right on a homogeneous machine and put every worker on an
        // efficiency core here — which costs milliseconds and reads as a random stall.
        var topology = new FakeTopology(
            [
                ProcessorClass.Efficiency,
                ProcessorClass.Efficiency,
                ProcessorClass.Performance,
                ProcessorClass.Efficiency,
                ProcessorClass.Performance,
                ProcessorClass.Efficiency,
                ProcessorClass.Performance,
                ProcessorClass.Performance
            ]
        );

        var placement = new ProcessorAffinityPlacement(topology);

        for (var ordinal = 0; ordinal < 4; ordinal++) {
            Assert.True(placement.TryPlace(ordinal, 4));
        }

        // The four performance cores, in index order within the class.
        Assert.Equal([2, 4, 6, 7], topology.Pinned);
    }

    [Fact]
    public void MoreWorkersThanProcessorsWrapsRatherThanRunningOffTheEnd() {
        var topology = new FakeTopology([ProcessorClass.Performance, ProcessorClass.Efficiency]);
        var placement = new ProcessorAffinityPlacement(topology);

        for (var ordinal = 0; ordinal < 5; ordinal++) {
            Assert.True(placement.TryPlace(ordinal, 5));
        }

        // Performance first, then the efficiency core, then round again. Nothing out of range: the
        // fake records whatever it is given, so an index past the end would show up here.
        Assert.Equal([0, 1, 0, 1, 0], topology.Pinned);
        Assert.All(topology.Pinned, processor => Assert.InRange(processor, 0, 1));
    }

    [Fact]
    public void APlatformWithNoAffinityIsAskedForNone() {
        // macOS's answer, and a browser's. The point is not only that TryPlace says false — it is
        // that TrySetAffinity is never reached, so a platform whose implementation would throw or
        // log on every call is not made to.
        var topology = new FakeTopology([ProcessorClass.Performance], supportsAffinity: false);
        var placement = new ProcessorAffinityPlacement(topology);

        Assert.False(placement.TryPlace(0, 1));
        Assert.Empty(topology.Pinned);
    }

    [Fact]
    public void NoProcessorsAtAllIsAnAnswerRatherThanADivideByZero() {
        // ⚠ Not a hypothetical: AvailableProcessors is what a container quota reports, and the code
        // that hands out processors picks `order[ordinal % order.Length]`. An empty order there is a
        // DivideByZeroException on the first worker of a machine nobody tested on.
        var topology = new FakeTopology([]);
        var placement = new ProcessorAffinityPlacement(topology);

        Assert.False(placement.TryPlace(0, 1));
        Assert.Empty(topology.Pinned);
    }

    [Fact]
    public void APlatformThatRefusesOneRequestSaysSoRatherThanClaimingIt() {
        // TrySetAffinity is allowed to fail per call — another process may hold the mask. The
        // placement has to pass that answer through, because JobScheduler.WorkersPlaced counts what
        // it returns and a placement that reported success regardless would make that counter a
        // number that cannot be wrong.
        var topology = new FakeTopology([ProcessorClass.Performance], acceptAffinity: false);
        var placement = new ProcessorAffinityPlacement(topology);

        Assert.False(placement.TryPlace(0, 1));
        Assert.Equal([0], topology.Pinned);
    }

    [Fact]
    public void PinningIsOffUnlessAskedFor() {
        // The default, and the one an ordinary desktop run gets.
        Assert.False(new AppConfig().PinWorkers);
        Assert.False(AppArguments.Parse(["--vixen-workers", "4"]).PinWorkers);
        Assert.True(AppArguments.Parse(["--vixen-pin-workers"]).PinWorkers);
    }

    [Fact]
    public void TheFlagCanTurnPinningOnAndNeverOff() {
        // A game that asked in code and a command line that did not mention it must not cancel out.
        var config = new AppConfig { PinWorkers = true };
        config.Apply(AppArguments.Parse(["--vixen-workers", "2"]));

        Assert.True(config.PinWorkers);
    }

    sealed class FakeTopology(
        ProcessorClass[] classes,
        bool supportsAffinity = true,
        bool acceptAffinity = true
    ) : IProcessorTopology {
        public List<int> Pinned { get; } = [];
        public int Cleared { get; private set; }

        public int AvailableProcessors => classes.Length;
        public int PhysicalCores => classes.Length;
        public int PerformanceCores => classes.Count(each => each == ProcessorClass.Performance);
        public bool SupportsAffinity => supportsAffinity;

        public ProcessorClass ClassOf(int processor) => classes[processor];

        public bool TrySetAffinity(int processor) {
            // Recorded before any judgement about it, so an out-of-range index reaches the assertion
            // instead of being turned into a throw the caller would have to interpret.
            Pinned.Add(processor);
            return acceptAffinity;
        }

        public void ClearAffinity() => Cleared++;
    }
}
