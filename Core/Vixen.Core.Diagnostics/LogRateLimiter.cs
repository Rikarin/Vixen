// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     Suppression of repeated events: the first few in each window get through, the rest are
///     counted, and the next one that gets through carries the count so the log can say
///     <c>… (repeated 4 812 times)</c>.
/// </summary>
/// <remarks>
///     <para>
///         Doc 13 asks for this by name, and the reason is not tidiness. One warning inside the
///         frame loop is sixty lines a second, which is a log file nobody can read, a console the
///         editor cannot keep up with, and — because the enabled path formats a string — real time
///         spent on the sixty thousandth copy of a message whose first copy said everything.
///     </para>
///     <para>
///         <b>Identity is the pair (category, event id)</b>, not the formatted text. ADR-008 gives
///         every call site a stable id from <c>docs/manual/log-events.md</c>, so the id already
///         identifies the message; hashing the formatted string instead would mean formatting it
///         before deciding to drop it, which is the cost this exists to avoid. The consequence is
///         that a warning whose arguments change every frame still collapses to one line plus a
///         count — for flood control that is the wanted behaviour, and the line that survives is the
///         most recent one rather than the first.
///     </para>
///     <para>
///         <b>It fails open.</b> The table of tracked events is bounded, and when it is full and
///         nothing in it is stale enough to evict, novel events are admitted untracked rather than
///         dropped. A rate limiter that loses the first report of a new error because a different
///         event filled its table is worse than no rate limiter.
///     </para>
///     <para>
///         Records at <see cref="LogLevel.Critical" /> are never suppressed. There is no flood of
///         them worth protecting against, and the one time there is, seeing all of them is the
///         point.
///     </para>
///     <para>
///         <b>A count is carried by the next record of the same identity</b>, so an event that
///         floods and then stops leaves its final tally unreported in the log itself. That is the
///         cost of never emitting a line nobody asked for, from a timer of this class's own;
///         <see cref="SuppressedCount" /> is the total, and a host that wants the tally reports it
///         at shutdown alongside the other dropped-record counters.
///     </para>
/// </remarks>
public sealed class LogRateLimiter {
    /// <summary>How many records with the same identity pass per window by default.</summary>
    public const int DefaultBurst = 4;

    /// <summary>How many distinct events are tracked by default.</summary>
    public const int DefaultMaxTrackedEvents = 1024;

    readonly Lock gate = new();
    readonly Dictionary<(string Category, int EventId), Bucket> buckets = [];
    readonly TimeProvider time;
    readonly long windowTimestampTicks;
    long suppressedCount;
    long untrackedCount;

    /// <summary>How long a window lasts.</summary>
    public TimeSpan Window { get; }

    /// <summary>How many records with the same identity pass per window.</summary>
    public int Burst { get; }

    /// <summary>How many distinct events are tracked at once.</summary>
    public int MaxTrackedEvents { get; }

    /// <summary>How many records this limiter has dropped, over its whole life.</summary>
    public long SuppressedCount {
        get {
            lock (gate) {
                return suppressedCount;
            }
        }
    }

    /// <summary>
    ///     How many records were admitted without being tracked because the table was full — the
    ///     count of times it failed open, which is what says the table is too small for the workload.
    /// </summary>
    public long UntrackedCount {
        get {
            lock (gate) {
                return untrackedCount;
            }
        }
    }

    /// <summary>How many distinct events are being tracked right now.</summary>
    public int TrackedEventCount {
        get {
            lock (gate) {
                return buckets.Count;
            }
        }
    }

    /// <summary>Creates a limiter.</summary>
    /// <param name="window">How long a window lasts. Must be positive.</param>
    /// <param name="burst">How many records with the same identity pass per window.</param>
    /// <param name="maxTrackedEvents">How many distinct events to track at once.</param>
    /// <param name="timeProvider">
    ///     Where the clock comes from. <see cref="TimeProvider.System" /> when null; a test hands in
    ///     its own so that a window can pass without the test taking that long.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is not positive.</exception>
    public LogRateLimiter(
        TimeSpan window,
        int burst = DefaultBurst,
        int maxTrackedEvents = DefaultMaxTrackedEvents,
        TimeProvider? timeProvider = null
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedEvents, 1);

        Window = window;
        Burst = burst;
        MaxTrackedEvents = maxTrackedEvents;
        time = timeProvider ?? TimeProvider.System;

        // Monotonic timestamps rather than wall-clock time: a machine that resyncs its clock, or a
        // laptop coming back from sleep, must not make a window a year long.
        windowTimestampTicks = (long)(window.TotalSeconds * time.TimestampFrequency);
    }

    /// <summary>Decides whether a record gets through.</summary>
    /// <param name="category">The logger category.</param>
    /// <param name="eventId">The event id, which with the category identifies the message.</param>
    /// <param name="level">The record's level. <see cref="LogLevel.Critical" /> is never suppressed.</param>
    /// <param name="suppressedCount">
    ///     How many records with this identity were dropped since the last one that got through.
    ///     Non-zero only on the record that ends a run of suppression, which is the one that should
    ///     be rendered with <c>(repeated N times)</c>.
    /// </param>
    /// <returns><see langword="true" /> if the record should be written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="category" /> is null.</exception>
    public bool TryAdmit(string category, EventId eventId, LogLevel level, out int suppressedCount) {
        ArgumentNullException.ThrowIfNull(category);

        suppressedCount = 0;

        if (level >= LogLevel.Critical) {
            return true;
        }

        var now = time.GetTimestamp();
        var key = (category, eventId.Id);

        lock (gate) {
            if (!buckets.ContainsKey(key) && buckets.Count >= MaxTrackedEvents && !TryEvictStale(now)) {
                untrackedCount++;

                return true;
            }

            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, key, out var existed);

            if (!existed || now - bucket.WindowStart >= windowTimestampTicks) {
                suppressedCount = existed ? bucket.Suppressed : 0;
                bucket = new() { WindowStart = now, Admitted = 1, Suppressed = 0 };

                return true;
            }

            if (bucket.Admitted < Burst) {
                bucket.Admitted++;

                return true;
            }

            bucket.Suppressed++;
            this.suppressedCount++;

            return false;
        }
    }

    /// <summary>
    ///     Forgets every tracked event, and with them every pending count. Counters keep their
    ///     totals.
    /// </summary>
    public void Reset() {
        lock (gate) {
            buckets.Clear();
        }
    }

    /// <summary>
    ///     Drops events whose window has passed with nothing waiting to be reported. Only ever
    ///     called with the table full, which on a well-behaved workload is never.
    /// </summary>
    bool TryEvictStale(long now) {
        var evicted = false;

        // Removing during enumeration is defined behaviour for Dictionary since .NET Core 3.0, and
        // is why this does not need a second list of keys to delete afterwards.
        foreach (var (key, bucket) in buckets) {
            if (bucket.Suppressed == 0 && now - bucket.WindowStart >= windowTimestampTicks) {
                buckets.Remove(key);
                evicted = true;
            }
        }

        return evicted;
    }

    struct Bucket {
        public long WindowStart;
        public int Admitted;
        public int Suppressed;
    }
}
