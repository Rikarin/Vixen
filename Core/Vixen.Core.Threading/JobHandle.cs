// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>A scheduled job, and an edge in the dependency graph.</summary>
/// <remarks>
///     <para>
///         Twelve bytes: an index into the scheduler's slot ring, the generation that slot was on
///         when the job was scheduled, and which scheduler owns it. The generation is what makes a
///         handle safe to keep — a slot that has been recycled reports a different generation, so a
///         stale handle reads as "already finished" rather than as some unrelated job that happens
///         to occupy the slot now.
///     </para>
///     <para>
///         The default value is the null handle. It is complete, completing it does nothing, and
///         depending on it is free — so <c>default</c> is the right thing to pass for "no
///         dependency" and the right thing to store in a field that has not been scheduled yet.
///     </para>
/// </remarks>
public readonly record struct JobHandle {
    internal readonly int Index;
    internal readonly int Version;
    internal readonly int SchedulerId;

    internal JobHandle(int index, int version, int schedulerId) {
        Index = index;
        Version = version;
        SchedulerId = schedulerId;
    }

    /// <summary>Whether this is the null handle — no job, already complete.</summary>
    public bool IsNull => Version == 0;

    /// <summary>Whether the job has finished. Never blocks.</summary>
    public bool IsCompleted => JobScheduler.IsHandleCompleted(in this);

    /// <summary>
    ///     Waits for the job to finish, executing other ready work in the meantime rather than
    ///     idling.
    /// </summary>
    /// <exception cref="JobExecutionException">The job, or one it depended on, threw.</exception>
    public void Complete() => JobScheduler.CompleteHandle(in this);

    /// <summary>Produces one handle that is complete when all of <paramref name="handles" /> are.</summary>
    /// <param name="handles">The handles to join. Null handles are ignored.</param>
    /// <returns>
    ///     A handle for an empty job depending on all of them, or the null handle if every input was
    ///     null.
    /// </returns>
    /// <exception cref="ArgumentException">The handles do not all belong to one scheduler.</exception>
    /// <remarks>
    ///     This costs a slot and a scheduling round-trip, so it is worth it when the join is reused —
    ///     as a dependency for several successors, or stored for a later <see cref="Complete" />.
    ///     To wait on a handful of handles once, complete each of them instead.
    /// </remarks>
    public static JobHandle Combine(params ReadOnlySpan<JobHandle> handles) {
        var scheduler = JobScheduler.OwnerOf(handles);
        return scheduler is null ? default : scheduler.CombineCore(handles);
    }

    /// <summary>Renders the handle as <c>#index.version</c>, or <c>#null</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsNull ? "#null" : $"#{Index}.{Version}";
}
