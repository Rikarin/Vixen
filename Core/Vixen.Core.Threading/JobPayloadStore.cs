// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Diagnostics;

namespace Vixen.Core.Threading;

/// <summary>Type-erased storage for scheduled job structs, and the call that runs one.</summary>
/// <remarks>
///     <para>
///         This is the whole answer to "how does a worker run a struct whose type it does not know".
///         A slot in the scheduler's ring holds a reference to the store its payload lives in; the
///         store is a <see cref="SequentialJobStore{TJob}" /> or a
///         <see cref="ParallelJobStore{TJob}" />, closed over the concrete job type, so running the
///         job is one virtual call landing on generic code that knows exactly what it is holding.
///     </para>
///     <para>
///         The alternative — <c>delegate*&lt;…&gt;</c> to a static generic method — saves the virtual
///         dispatch and costs <c>AllowUnsafeBlocks</c> plus a function pointer whose lifetime nobody
///         can see. One virtual call per job, against a job body that is doing enough work to be
///         worth scheduling, is not the thing that will be slow.
///     </para>
///     <para>
///         The payload array is one element per slot in the ring, so a scheduled job is a struct
///         copy into a preallocated array. Nothing on the scheduling path allocates.
///     </para>
/// </remarks>
abstract class JobPayloadStore {
    /// <summary>The profiling key every job in this store records under.</summary>
    internal ProfilingKey Key { get; }

    private protected JobPayloadStore(ProfilingKey key) => Key = key;

    /// <summary>Runs one batch of the job in <paramref name="slot" />.</summary>
    /// <param name="slot">Which payload to run.</param>
    /// <param name="start">The first index of the batch. Ignored by non-parallel jobs.</param>
    /// <param name="count">How many indices are in the batch. Ignored by non-parallel jobs.</param>
    internal abstract void Execute(int slot, int start, int count);

    /// <summary>Drops the payload in <paramref name="slot" /> once it can no longer run.</summary>
    /// <param name="slot">Which payload to drop.</param>
    internal abstract void Release(int slot);
}

/// <summary>Storage for one concrete <see cref="IJob" /> type.</summary>
/// <typeparam name="TJob">The job type.</typeparam>
sealed class SequentialJobStore<TJob> : JobPayloadStore where TJob : struct, IJob {
    readonly TJob[] payloads;

    internal SequentialJobStore(int capacity) : base(SequentialJobType<TJob>.Key) => payloads = new TJob[capacity];

    internal void Store(int slot, in TJob job) => payloads[slot] = job;

    internal override void Execute(int slot, int start, int count) => payloads[slot].Execute();

    internal override void Release(int slot) {
        // A job that captured a reference would otherwise keep it alive until the slot is reused,
        // which for a rarely scheduled job type is indistinguishable from a leak.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TJob>()) {
            payloads[slot] = default;
        }
    }
}

/// <summary>Storage for one concrete <see cref="IJobParallelFor" /> type.</summary>
/// <typeparam name="TJob">The job type.</typeparam>
sealed class ParallelJobStore<TJob> : JobPayloadStore where TJob : struct, IJobParallelFor {
    readonly TJob[] payloads;

    internal ParallelJobStore(int capacity) : base(ParallelJobType<TJob>.Key) => payloads = new TJob[capacity];

    internal void Store(int slot, in TJob job) => payloads[slot] = job;

    internal override void Execute(int slot, int start, int count) {
        // By reference: every batch of every parallel job shares one payload, and copying it per
        // index would both cost and quietly break jobs whose fields are meant to be shared.
        ref var job = ref payloads[slot];
        var end = start + count;

        for (var index = start; index < end; index++) {
            job.Execute(index);
        }
    }

    internal override void Release(int slot) {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TJob>()) {
            payloads[slot] = default;
        }
    }
}

/// <summary>Identity for one <see cref="IJob" /> type, assigned once and then a static field read.</summary>
/// <typeparam name="TJob">The job type.</typeparam>
/// <remarks>
///     Two identifier spaces — this and <c>ParallelJobType&lt;TJob&gt;</c> — because a struct is
///     allowed to implement both job interfaces and the two are stored separately. The profiling key
///     is registered here too, so the frame graph shows <c>Job.CullingJob</c> without a single
///     reflection call at runtime: the name is resolved once, when the type is first scheduled.
/// </remarks>
static class SequentialJobType<TJob> where TJob : struct, IJob {
    internal static readonly int Id = JobTypeIds.NextSequential();
    internal static readonly ProfilingKey Key = ProfilingKey.Register("Job." + typeof(TJob).Name);
}

/// <summary>Identity for one <see cref="IJobParallelFor" /> type.</summary>
/// <typeparam name="TJob">The job type.</typeparam>
static class ParallelJobType<TJob> where TJob : struct, IJobParallelFor {
    internal static readonly int Id = JobTypeIds.NextParallel();
    internal static readonly ProfilingKey Key = ProfilingKey.Register("JobFor." + typeof(TJob).Name);
}

/// <summary>The counters behind the per-type identity classes.</summary>
static class JobTypeIds {
    internal const int MaxJobTypes = 256;

    static int sequential = -1;
    static int parallel = -1;

    internal static int NextSequential() => Next(ref sequential);
    internal static int NextParallel() => Next(ref parallel);

    static int Next(ref int counter) {
        var id = Interlocked.Increment(ref counter);

        if (id >= MaxJobTypes) {
            throw new InvalidOperationException(
                $"More than {MaxJobTypes} distinct job types have been scheduled. The scheduler sizes "
                + "its per-type payload tables from this bound; raise JobTypeIds.MaxJobTypes if a "
                + "program genuinely has this many."
            );
        }

        return id;
    }
}
