// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>
///     Where a worker thread should run, asked of the worker itself as it starts.
/// </summary>
/// <remarks>
///     <para>
///         <b>The seam exists because affinity is a property of the calling thread.</b> Every
///         per-OS primitive behind this — <c>SetThreadGroupAffinity</c>, <c>sched_setaffinity(0, …)</c>
///         — pins <em>whoever calls it</em>. So the placement cannot be applied by the thread that
///         constructs the scheduler, and a design that computed a table up front and applied it from
///         there would pin the constructing thread <see cref="JobScheduler.WorkerCount" /> times and
///         leave every worker exactly where it was, with no counter anywhere reading differently.
///         <see cref="TryPlace" /> is therefore called <em>on</em> the worker, before its first take.
///     </para>
///     <para>
///         <b>And because this assembly may not know what a processor is.</b> The topology lives in
///         <c>Vixen.Platform</c>, which is above this one; an interface here, implemented up there,
///         is the same dependency the other way round — the shape <c>JobAccess</c> already uses for
///         resource ids and <c>IRemoteLogTransport</c> for the inspector protocol.
///     </para>
///     <para>
///         ⚠ <b>Pinning is not free and is not a default.</b> A pinned worker cannot be moved off a
///         core the OS has given to something else, so on a machine running anything besides the
///         game it is a pessimisation rather than an optimisation — which is why
///         <see cref="JobScheduler" /> has no placement unless a caller supplies one, and why
///         <see cref="TryPlace" /> returning <see langword="false" /> is an ordinary answer rather
///         than a failure. macOS gives quality-of-service classes instead of affinity masks and
///         answers <see langword="false" /> everywhere.
///     </para>
/// </remarks>
public interface IWorkerPlacement {
    /// <summary>Places the calling worker thread, which is the one being started.</summary>
    /// <param name="ordinal">Which worker this is, in <c>[0, workerCount)</c>.</param>
    /// <param name="workerCount">How many workers the scheduler has, so a policy can spread them.</param>
    /// <returns>
    ///     Whether the thread was actually placed. <see langword="false" /> where the platform has no
    ///     answer, which is not an error and is what <see cref="JobScheduler.WorkersPlaced" />
    ///     counts the difference of.
    /// </returns>
    /// <remarks>
    ///     Called once per worker, on that worker, before it takes its first work item. It must not
    ///     block: the pool is not running until every worker has been through here.
    /// </remarks>
    bool TryPlace(int ordinal, int workerCount);

    /// <summary>Undoes <see cref="TryPlace" /> for the calling worker thread.</summary>
    /// <remarks>
    ///     Called on the worker as it stops, and only where <see cref="TryPlace" /> answered
    ///     <see langword="true" /> — so an implementation never has to ask whether there is anything
    ///     to undo. A worker thread does not outlive its scheduler, so this is mostly for a host that
    ///     rebuilds one.
    /// </remarks>
    void Release();
}
