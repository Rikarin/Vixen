// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>Which of the scheduler's two tiers a job goes in.</summary>
/// <remarks>
///     <para>
///         <b>Two tiers, because there are two answers to one question:</b> does this have to finish
///         before the frame ends? <see cref="Frame" /> is everything a frame is made of and it is the
///         default, so a caller that has not thought about it gets the tier that cannot be starved.
///         <see cref="Background" /> is work that has to finish eventually — an import, a bake, an
///         unwrap, a decode — and would rather be late than make a frame late.
///     </para>
///     <para>
///         ⚠ <b>Deferral, not preemption.</b> A job is a struct's <c>Execute</c> running on a worker
///         thread; there is no safe point to suspend it at and no portable way to suspend a thread
///         mid-call anyway. So a <see cref="Background" /> job that has <i>started</i> runs to
///         completion, and what the tier buys is only which item a thread picks up <i>next</i>. The
///         consequence is worth stating rather than discovering: one background job that takes a
///         hundred milliseconds delays the frame work behind it by up to a hundred milliseconds on
///         that one thread, whatever tier it is in. Splitting long work into batches is what makes
///         the tier effective, and <see cref="JobScheduler.ScheduleParallel{TJob}(in TJob, int, int,
///         JobHandle, JobPriority)" /> is how.
///     </para>
///     <para>
///         ⚠ <b>The tier is not a correctness device.</b> Dependency edges are unchanged: a
///         <see cref="Frame" /> job that depends on a <see cref="Background" /> job waits for it, and
///         no priority makes it wait less. If the answer wanted is "this must not run yet", that is
///         an edge, not a tier.
///     </para>
/// </remarks>
public enum JobPriority {
    /// <summary>Work the current frame is waiting for. The default, and never deferred.</summary>
    Frame = 0,

    /// <summary>
    ///     Work that has to finish eventually, and would rather be late than make a frame late.
    /// </summary>
    /// <remarks>
    ///     Taken only when there is no frame work a thread can reach — plus a fairness share, so a
    ///     program that never stops scheduling frame work still makes progress here rather than
    ///     stopping forever. Workers also keep one of their number out of this tier, so a burst of
    ///     background jobs cannot occupy the whole pool.
    /// </remarks>
    Background = 1
}
