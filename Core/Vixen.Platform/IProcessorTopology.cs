// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>What kind of core a logical processor sits on.</summary>
/// <remarks>
///     Heterogeneous CPUs are now the common case rather than the mobile exception — Apple silicon,
///     Intel's P/E split, and every ARM big.LITTLE phone. Scheduling a frame-critical worker onto an
///     efficiency core costs several milliseconds and looks exactly like a random stall.
/// </remarks>
public enum ProcessorClass : byte {
    /// <summary>The platform does not distinguish, or does not say.</summary>
    Unknown = 0,

    /// <summary>A performance core.</summary>
    Performance = 1,

    /// <summary>An efficiency core.</summary>
    Efficiency = 2
}

/// <summary>How many processors there are, of what kind, and whether we may choose.</summary>
/// <remarks>
///     <para>
///         This is the contract half of the thread-pinning work that
///         <c>docs/plan/03</c> deferred out of <c>Vixen.Core.Threading</c> — the job scheduler
///         cannot ask about cores without something like this, and it could not depend on a platform
///         layer that did not exist. It stays a query with a fallback rather than an assumption:
///         <see cref="TrySetAffinity" /> returning <see langword="false" /> is the normal answer in
///         a browser, under a container CPU quota, and on macOS, which offers quality-of-service
///         classes instead of affinity masks and is right to.
///     </para>
///     <para>
///         <see cref="AvailableProcessors" /> is the number to size a worker pool from, not
///         <see cref="Environment.ProcessorCount" />: in a container the two differ, and spawning
///         sixty-four workers against a two-core quota produces a job system that spends its time
///         being descheduled.
///     </para>
/// </remarks>
public interface IProcessorTopology {
    /// <summary>How many logical processors this process may actually run on.</summary>
    int AvailableProcessors { get; }

    /// <summary>How many physical cores there are, or <see cref="AvailableProcessors" /> where the
    /// platform does not distinguish.</summary>
    int PhysicalCores { get; }

    /// <summary>How many performance cores there are, or <c>0</c> where the platform does not say.</summary>
    int PerformanceCores { get; }

    /// <summary>Whether <see cref="TrySetAffinity" /> can do anything on this platform.</summary>
    bool SupportsAffinity { get; }

    /// <summary>What kind of core a logical processor sits on.</summary>
    /// <param name="processor">A logical processor index, <c>[0, AvailableProcessors)</c>.</param>
    ProcessorClass ClassOf(int processor);

    /// <summary>Pins the calling thread to one logical processor.</summary>
    /// <param name="processor">A logical processor index, <c>[0, AvailableProcessors)</c>.</param>
    /// <returns><see langword="false" /> where the platform does not allow it, which is not an
    /// error.</returns>
    bool TrySetAffinity(int processor);

    /// <summary>Undoes a previous <see cref="TrySetAffinity" /> for the calling thread.</summary>
    void ClearAffinity();
}
