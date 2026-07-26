// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.ExceptionServices;

namespace Vixen.Core.Threading;

/// <summary>One entry in the scheduler's job ring: a job's bookkeeping, minus the job itself.</summary>
/// <remarks>
///     <para>
///         A class, not a struct in an array, because the successor list and the gate are reference
///         types anyway and the ring is preallocated once. Nothing here is ever collected during a
///         run.
///     </para>
///     <para>
///         <see cref="Gate" /> guards the three things that have to agree with each other:
///         <see cref="Version" />, <see cref="IsComplete" />, and <see cref="Successors" />. Adding
///         an edge to a job that is finishing at that moment is the whole difficulty of a job graph,
///         and a lock-free continuation list solves it with a CAS loop plus an ABA guard plus a
///         separately allocated link node per edge, all so that a few hundred edges per frame can be
///         added without blocking. This takes the lock instead. It is uncontended in the ordinary
///         case — the scheduling thread and one completing worker — and it is the difference between
///         a graph whose correctness can be read off the code and one whose correctness is an
///         argument.
///     </para>
/// </remarks>
sealed class JobSlot {
    /// <summary>Guards <see cref="Version" />, <see cref="IsComplete" /> and <see cref="Successors" />.</summary>
    internal readonly Lock Gate = new();

    /// <summary>Slots waiting on this one.</summary>
    /// <remarks>
    ///     Preallocated for the common fan-out and cleared rather than replaced, so a steady-state
    ///     frame adds no garbage unless a job genuinely has more successors than this.
    /// </remarks>
    internal readonly List<int> Successors = new(4);

    /// <summary>Which generation of this slot is live. Odd numbers are never skipped; it just counts.</summary>
    /// <remarks>
    ///     A handle carries the generation it was issued for. Once the slot is reused the generation
    ///     moves on and every older handle reads as complete, which is true — the slot could not have
    ///     been reused otherwise.
    /// </remarks>
    internal int Version;

    /// <summary>Whether the job has finished. Only meaningful together with <see cref="Version" />.</summary>
    internal bool IsComplete = true;

    /// <summary>Where the job struct lives, and the call that runs it.</summary>
    internal JobPayloadStore? Store;

    /// <summary>How many dependencies have yet to finish. The job becomes runnable at zero.</summary>
    /// <remarks>
    ///     Held one above the true count while the graph edges are being added, so a dependency that
    ///     finishes mid-setup cannot start the job before the rest of its edges exist.
    /// </remarks>
    internal int PendingDependencies;

    /// <summary>How many work items are still outstanding. The job is complete at zero.</summary>
    internal int PendingWork;

    /// <summary>How many work items the job is split into. One, unless it is a parallel-for.</summary>
    internal int BatchCount;

    /// <summary>How many indices each batch covers.</summary>
    internal int BatchSize;

    /// <summary>The parallel-for length. Zero for an ordinary job.</summary>
    internal int Length;

    /// <summary>What the job threw, if it threw; or what it inherited from a failed dependency.</summary>
    internal ExceptionDispatchInfo? Failure;

    /// <summary>Prepares the slot for a new job and returns the generation to issue handles for.</summary>
    /// <param name="store">Where the payload was written.</param>
    /// <param name="batchCount">How many work items the job splits into.</param>
    /// <param name="batchSize">How many indices per work item.</param>
    /// <param name="length">The parallel-for length, or zero.</param>
    /// <returns>The new generation.</returns>
    internal int Reset(JobPayloadStore? store, int batchCount, int batchSize, int length) {
        lock (Gate) {
            Version++;
            IsComplete = false;
            Store = store;
            Failure = null;
            BatchCount = batchCount;
            BatchSize = batchSize;
            Length = length;
            PendingWork = batchCount;

            // One for the scheduler itself, released once every edge has been added.
            PendingDependencies = 1;
            Successors.Clear();
            return Version;
        }
    }
}
