// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Placement;

/// <summary>A range of UDP ports, handed out one at a time and given back when a realm stops.</summary>
/// <remarks>
///     <para>
///         The one piece of configuration <c>Placement.Process</c> needs, and the same shape as the
///         node port range ADR-019 names as Kubernetes's single cluster prerequisite. A realm's
///         endpoint has to be knowable by the orchestrator <em>before</em> the process exists — a
///         client is told where to go by placement, not by the realm — so the pool allocates and the
///         realm is told, rather than the realm binding port zero and reporting back.
///     </para>
///     <para>
///         ⚠ <b>A rented port is not a bound one.</b> Something else on the machine may hold it, and
///         the realm will fail to start. That is reported as a start failure rather than papered over
///         with a retry loop, because on a machine where the range overlaps something else every
///         start is a coin toss and the useful outcome is finding that out on the first one.
///     </para>
/// </remarks>
public sealed class PortPool {
    readonly HashSet<int> rented = [];
    readonly int first;
    readonly int last;

    int next;

    /// <summary>The first port in the range.</summary>
    public int First => first;

    /// <summary>The last port in the range, inclusive.</summary>
    public int Last => last;

    /// <summary>How many ports are currently out.</summary>
    public int RentedCount {
        get {
            lock (rented) {
                return rented.Count;
            }
        }
    }

    /// <summary>How many ports the range holds.</summary>
    public int Capacity => last - first + 1;

    /// <summary>Takes a range.</summary>
    /// <param name="first">The first port.</param>
    /// <param name="last">The last port, inclusive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The range is empty, or reaches outside 1–65535.
    /// </exception>
    public PortPool(int first = 7800, int last = 7899) {
        ArgumentOutOfRangeException.ThrowIfLessThan(first, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(last, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(last, first);

        this.first = first;
        this.last = last;
        next = first;
    }

    /// <summary>Takes the next free port.</summary>
    /// <param name="port">The port, on success.</param>
    /// <returns>Whether the range had one left.</returns>
    /// <remarks>
    ///     Round-robin rather than lowest-free, so a port is not reused the instant it is returned.
    ///     A realm that has just stopped may still have datagrams in flight toward it, and a new
    ///     realm on the same port would receive them — which presents as one shard occasionally
    ///     seeing packets meant for another and is not a bug anybody diagnoses quickly.
    /// </remarks>
    public bool TryRent(out int port) {
        lock (rented) {
            for (var attempt = 0; attempt < Capacity; attempt++) {
                var candidate = next;

                next = next == last ? first : next + 1;

                if (rented.Add(candidate)) {
                    port = candidate;

                    return true;
                }
            }
        }

        port = 0;

        return false;
    }

    /// <summary>Gives a port back.</summary>
    /// <param name="port">The port. One that was never rented is ignored.</param>
    public void Return(int port) {
        lock (rented) {
            rented.Remove(port);
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"ports {first}–{last}, {RentedCount} out");
}
