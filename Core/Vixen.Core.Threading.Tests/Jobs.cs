// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Core.Threading.Tests;

/// <summary>Stamps a global sequence number where it ran, so relative order can be asserted.</summary>
/// <remarks>
///     Stamped on entry rather than on exit. A job's predecessor has finished before the job starts,
///     so a successor's stamp is strictly greater than every predecessor's — which is exactly the
///     ordering the dependency graph promises, and nothing weaker.
/// </remarks>
struct StampJob(int[] stamps, int index, StrongBox<int> clock) : IJob {
    public void Execute() => stamps[index] = Interlocked.Increment(ref clock.Value);
}

/// <summary>Waits for a gate, then counts. Lets a test hold slots without holding threads.</summary>
struct GatedIncrementJob(ManualResetEventSlim gate, StrongBox<int> counter) : IJob {
    public void Execute() {
        gate.Wait();
        Interlocked.Increment(ref counter.Value);
    }
}

/// <summary>Counts how many times each index was visited.</summary>
struct VisitJob(int[] visits) : IJobParallelFor {
    public void Execute(int index) => Interlocked.Increment(ref visits[index]);
}

/// <summary>Writes <c>index</c> into <c>index</c>, so a wrong batch range shows up as a hole.</summary>
struct WriteIndexJob(int[] written) : IJobParallelFor {
    public void Execute(int index) => written[index] = index + 1;
}

/// <summary>Adds one to a shared counter.</summary>
struct IncrementJob(StrongBox<int> counter) : IJob {
    public void Execute() => Interlocked.Increment(ref counter.Value);
}

/// <summary>Counts how many copies of itself were running at once, and remembers the most.</summary>
/// <remarks>
///     The peak is a compare-and-swap loop rather than a `Math.Max` on a plain field, because two
///     threads reading the same stale maximum and both writing theirs would lose the larger one —
///     which is the direction that turns a failing bound into a passing one.
/// </remarks>
struct ConcurrencyProbeJob(StrongBox<int> live, StrongBox<int> peak, int spins) : IJobParallelFor {
    public void Execute(int index) {
        var now = Interlocked.Increment(ref live.Value);
        var seen = Volatile.Read(ref peak.Value);

        while (now > seen) {
            var previous = Interlocked.CompareExchange(ref peak.Value, now, seen);

            if (previous == seen) {
                break;
            }

            seen = previous;
        }

        Thread.SpinWait(spins);
        Interlocked.Decrement(ref live.Value);
    }
}

/// <summary>Records that it ran at all.</summary>
struct FlagJob(bool[] flags, int index) : IJob {
    public void Execute() => flags[index] = true;
}

/// <summary>Throws, on purpose.</summary>
struct ThrowingJob : IJob {
    public void Execute() => throw new InvalidOperationException("The job threw on purpose.");
}

/// <summary>Throws for one particular index.</summary>
struct ThrowingParallelJob(int failAt) : IJobParallelFor {
    public void Execute(int index) {
        if (index == failAt) {
            throw new InvalidOperationException($"The job threw on purpose at {index}.");
        }
    }
}

/// <summary>Burns a little time, so the workers actually overlap.</summary>
struct SpinJob(StrongBox<int> counter, int spins) : IJob {
    public void Execute() {
        Thread.SpinWait(spins);
        Interlocked.Increment(ref counter.Value);
    }
}
