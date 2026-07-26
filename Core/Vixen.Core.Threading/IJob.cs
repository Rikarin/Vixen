// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>A unit of work the scheduler runs once, on whichever thread picks it up.</summary>
/// <remarks>
///     <para>
///         Implement this on a <see langword="struct" />. The scheduler is generic over the
///         implementing type, so the call to <see cref="Execute" /> is a direct call on a value in an
///         array of that exact type: no boxing, no delegate, no closure, and no allocation on the
///         path from <see cref="JobScheduler.Schedule{TJob}(in TJob, JobHandle)" /> to the work
///         running.
///     </para>
///     <para>
///         The struct is copied into the scheduler when it is scheduled, so mutating your copy
///         afterwards changes nothing. Results come back through whatever the job's fields point at —
///         an array, a <c>NativeArray&lt;T&gt;</c>, a shared counter — not through the struct itself.
///     </para>
/// </remarks>
public interface IJob {
    /// <summary>Runs the work.</summary>
    void Execute();
}

/// <summary>A unit of work the scheduler runs once per index, spread across threads.</summary>
/// <remarks>
///     <para>
///         The scheduler splits <c>[0, length)</c> into batches and hands each batch out as a
///         separate work item, so idle threads steal batches rather than waiting for a fixed
///         partition. <see cref="Execute" /> is called once for every index in <c>[0, length)</c>,
///         exactly once, in no guaranteed order and on no guaranteed thread.
///     </para>
///     <para>
///         The same job struct is shared by every batch. Two indices may therefore run at the same
///         time on different threads, which is exactly the point — and which means a
///         <see cref="IJobParallelFor" /> that writes anywhere other than <c>index</c>'s own element
///         is a data race that no part of this library can see.
///     </para>
/// </remarks>
public interface IJobParallelFor {
    /// <summary>Runs the work for one index.</summary>
    /// <param name="index">The index, in <c>[0, length)</c>.</param>
    void Execute(int index);
}
