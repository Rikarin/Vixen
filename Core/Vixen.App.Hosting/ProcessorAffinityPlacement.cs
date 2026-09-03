// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>
///     Pins each job worker to one logical processor, performance cores first.
/// </summary>
/// <remarks>
///     <para>
///         The half of thread affinity that <c>Vixen.Core.Threading</c> could not have: the job
///         system may not know what a processor is, and <c>Vixen.Platform</c> may not know what a
///         worker is. This is the one place in the tree that knows both, which is why it is here and
///         not in either of them.
///     </para>
///     <para>
///         <b>The order is performance cores first, and that is the whole policy.</b> On a
///         heterogeneous CPU — Apple silicon, Intel's P/E split, every big.LITTLE phone — a
///         frame-critical worker that lands on an efficiency core costs several milliseconds and
///         looks exactly like a random stall. Workers are handed processors in that order and wrap
///         if there are more workers than processors.
///     </para>
///     <para>
///         ⚠ <b>No processor is reserved for the main thread, because the arithmetic already does
///         it.</b> <c>AppBuilder</c> asks for <c>AvailableProcessors - 1</c> workers, so the last
///         processor in the order has no worker pinned to it and is where an unpinned main thread
///         naturally ends up. A reservation on top of that would be the same subtraction applied
///         twice, and would idle a core on any host that chose its own worker count.
///     </para>
///     <para>
///         ⚠ <b>Opt in, because pinning is a pessimisation on a machine that is running anything
///         else.</b> A pinned worker cannot be moved off a core the OS has given to a browser, a
///         compiler or another game, so it waits behind them instead of being migrated. It is worth
///         asking for on a console or a dedicated server and rarely on a desktop, which is why
///         <c>AppConfig.PinWorkers</c> is false unless somebody says otherwise.
///     </para>
/// </remarks>
sealed class ProcessorAffinityPlacement : IWorkerPlacement {
    readonly IProcessorTopology topology;
    readonly int[] order;

    /// <summary>Builds a placement over a platform's topology.</summary>
    /// <param name="topology">The platform's processor topology.</param>
    internal ProcessorAffinityPlacement(IProcessorTopology topology) {
        ArgumentNullException.ThrowIfNull(topology);
        this.topology = topology;

        // Computed once, on whichever thread builds this — it reads the topology and touches no
        // thread state, so it is the part that does not have to happen on the worker.
        order = topology.SupportsAffinity
            ? [.. Enumerable
                .Range(0, Math.Max(0, topology.AvailableProcessors))
                .OrderBy(processor => Rank(topology.ClassOf(processor)))
                .ThenBy(processor => processor)]
            : [];
    }

    /// <inheritdoc />
    public bool TryPlace(int ordinal, int workerCount) {
        // Empty where the platform said no, and — the case worth guarding rather than assuming
        // away — where it reported no processors at all, which would otherwise be a modulo by zero
        // on a machine whose quota could not be read.
        if (order.Length == 0) {
            return false;
        }

        return topology.TrySetAffinity(order[ordinal % order.Length]);
    }

    /// <inheritdoc />
    public void Release() => topology.ClearAffinity();

    // Performance first, then whatever the platform would not classify, then efficiency. Unknown
    // sits in the middle rather than last because it is what a homogeneous machine reports for
    // every core it has, and sorting those behind the efficiency cores of a machine that has none
    // would be ranking them against a class that is not there.
    static int Rank(ProcessorClass processorClass) => processorClass switch {
        ProcessorClass.Performance => 0,
        ProcessorClass.Unknown => 1,
        _ => 2
    };
}
