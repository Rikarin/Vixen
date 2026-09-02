// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>
///     The window in which every job this thread schedules carries one <see cref="JobAccess" />.
/// </summary>
/// <remarks>
///     <para>
///         A scope rather than a parameter on the four <c>Schedule</c> overloads, because the caller
///         that knows the access and the caller that schedules the work are usually not the same one.
///         The ECS is the case that decided it: the system runner knows what a system declared and
///         brackets the call to its <c>Update</c>, and the system inside schedules whatever it likes
///         without having to thread a declaration through code that does not care.
///     </para>
///     <para>
///         ⚠ <b>The scope belongs to the thread that opened it, and does not travel into the job.</b>
///         A job that schedules more jobs from a worker thread is scheduling them undeclared, and so
///         unchecked. Nesting is fine: an inner scope replaces the outer one and
///         <see cref="Dispose" /> puts it back.
///     </para>
///     <para>
///         Disposing twice, or disposing the default value, does nothing. In a build with
///         <see cref="JobScheduler.SafetyChecksEnabled" /> false the whole thing is a no-op that the
///         jitter removes.
///     </para>
/// </remarks>
public readonly struct JobAccessScope : IDisposable {
    readonly JobScheduler? scheduler;
    readonly JobAccess? previous;

    internal JobAccessScope(JobScheduler scheduler, JobAccess? previous) {
        this.scheduler = scheduler;
        this.previous = previous;
    }

    /// <summary>Puts back whatever declaration was in force before.</summary>
    public void Dispose() {
        if (scheduler is not null) {
            JobScheduler.RestoreDeclaredAccess(previous);
        }
    }
}
