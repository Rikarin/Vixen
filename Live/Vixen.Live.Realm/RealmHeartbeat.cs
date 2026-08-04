// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Realms;

/// <summary>One sample of what a shard is costing. Doc 27 § Health.</summary>
/// <remarks>
///     ⚠ <b>None of these numbers is a second measurement system.</b> Every one of them is already an
///     instrument in <c>Vixen.Net.Telemetry</c> and doc 13; the heartbeat is a <em>sample of the
///     meter</em>, so a shard's health and its traces cannot disagree about what its tick cost.
/// </remarks>
/// <param name="Shard">Which shard.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="Population">How many players are on it.</param>
/// <param name="TickP99Milliseconds">The tail, which is the number that matters.</param>
/// <param name="TickMeanMilliseconds">The middle, which is the number that flatters.</param>
/// <param name="Blocked">
///     How many players a drain could not move — doc 27 § Drain's escalation input.
/// </param>
/// <param name="SampledAt">When.</param>
public readonly record struct RealmHealth(
    ShardId Shard,
    ShardState State,
    int Population,
    double TickP99Milliseconds,
    double TickMeanMilliseconds,
    int Blocked,
    DateTimeOffset SampledAt
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Shard} {State}: {Population} players, tick p99 {TickP99Milliseconds:F2} ms"
        );
}

/// <summary>The two-second sample, and the window it is computed over.</summary>
/// <remarks>
///     <para>
///         <b>p99 rather than a mean, because the mean is the number that flatters.</b> A shard whose
///         average tick is 4 ms and whose p99 is 40 ms is one where every player sees a hitch twice a
///         second, and doc 27 § Health's whole point is that such a shard should stop being a
///         placement candidate <em>before</em> it stops being playable. A mean never says so.
///     </para>
///     <para>
///         A fixed ring rather than a histogram: the window is a few hundred ticks, sorting it costs
///         microseconds every two seconds, and an approximate quantile structure would be a second
///         thing to be wrong about for a measurement that is already only advisory.
///     </para>
/// </remarks>
public sealed class RealmHeartbeat {
    /// <summary>What doc 27 § Health specifies, and what <see cref="ShardState.Lost" /> counts.</summary>
    public static TimeSpan DefaultInterval => TimeSpan.FromSeconds(2);

    readonly double[] window;
    readonly double[] sorted;

    TimeSpan sinceLastSample;
    int count;
    int next;

    /// <summary>How often a sample is due.</summary>
    public TimeSpan Interval { get; }

    /// <summary>How many ticks the window holds.</summary>
    public int WindowSize => window.Length;

    /// <summary>How many samples have been taken.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Stands a heartbeat up.</summary>
    /// <param name="interval">How often to sample, or null for <see cref="DefaultInterval" />.</param>
    /// <param name="windowSize">
    ///     How many ticks the quantile is computed over. The default is two hundred and fifty-six,
    ///     which at 30 Hz is about eight seconds — four heartbeats, so a hitch is reported by more
    ///     than one of them and a fleet view does not have to catch it in a single frame.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The interval or the window is not positive.</exception>
    public RealmHeartbeat(TimeSpan? interval = null, int windowSize = 256) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);

        Interval = interval ?? DefaultInterval;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero);

        window = new double[windowSize];
        sorted = new double[windowSize];
    }

    /// <summary>Records how long a tick took.</summary>
    /// <param name="tick">The frame's own elapsed time.</param>
    public void Observe(TimeSpan tick) {
        window[next] = tick.TotalMilliseconds;
        next = next == window.Length - 1 ? 0 : next + 1;

        if (count < window.Length) {
            count++;
        }
    }

    /// <summary>Advances the clock and says whether a sample is due.</summary>
    /// <param name="elapsed">How long since the last call.</param>
    /// <returns>Whether the caller should send one.</returns>
    /// <remarks>
    ///     ⚠ <b>The remainder is kept, not reset.</b> A realm updating at 30 Hz never lands exactly on
    ///     two seconds, and discarding the overshoot each time would make the heartbeat slowly slower
    ///     than it claims — which is how three missed heartbeats become a shard declared
    ///     <see cref="ShardState.Lost" /> while it is still simulating happily.
    /// </remarks>
    public bool IsDue(TimeSpan elapsed) {
        sinceLastSample += elapsed;

        if (sinceLastSample < Interval) {
            return false;
        }

        sinceLastSample -= Interval;
        SampleCount++;

        return true;
    }

    /// <summary>The tail of the window, in milliseconds.</summary>
    /// <returns>The 99th percentile, or zero before any tick has been observed.</returns>
    public double TickP99() => Quantile(0.99);

    /// <summary>The middle of the window, in milliseconds.</summary>
    /// <returns>The mean, or zero before any tick has been observed.</returns>
    public double TickMean() {
        if (count == 0) {
            return 0;
        }

        var total = 0.0;

        for (var index = 0; index < count; index++) {
            total += window[index];
        }

        return total / count;
    }

    double Quantile(double quantile) {
        if (count == 0) {
            return 0;
        }

        Array.Copy(window, sorted, count);
        Array.Sort(sorted, 0, count);

        // Nearest-rank. With 256 samples the 99th is the 254th of them, and interpolating between
        // two neighbours would be precision this measurement does not have.
        var rank = (int)Math.Ceiling(quantile * count) - 1;

        return sorted[Math.Clamp(rank, 0, count - 1)];
    }
}
