// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Time;

/// <summary>
///     Smoothed round-trip time and jitter, from samples.
/// </summary>
/// <remarks>
///     <para>
///         The estimator TCP uses (RFC 6298): an exponentially weighted average of the samples, and
///         beside it an exponentially weighted average of how far each sample fell from that average.
///         The second number is the one that matters here — the interpolation buffer and the tick
///         lead are sized from the <i>variance</i>, because a steady 200 ms link is easy and a link
///         that swings between 40 and 90 ms is not.
///     </para>
///     <para>
///         The weights are the standard 1/8 and 1/4, which is a deliberate refusal to invent: they
///         have been load-bearing in every TCP stack for thirty years, and a netcode-specific
///         constant chosen by eye would be worse and unjustifiable.
///     </para>
/// </remarks>
public sealed class RoundTripEstimator {
    const double SampleWeight = 1.0 / 8.0;
    const double VarianceWeight = 1.0 / 4.0;

    double roundTripTicks;
    double varianceTicks;

    /// <summary>The smoothed round trip.</summary>
    public TimeSpan RoundTrip => TimeSpan.FromTicks((long)roundTripTicks);

    /// <summary>Half the smoothed round trip — how old the newest thing from the server is.</summary>
    public TimeSpan OneWay => TimeSpan.FromTicks((long)(roundTripTicks / 2));

    /// <summary>How far samples typically fall from the average. The number that sizes buffers.</summary>
    public TimeSpan Jitter => TimeSpan.FromTicks((long)varianceTicks);

    /// <summary>How many samples have been taken.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Whether anything has been measured yet.</summary>
    public bool HasSamples => SampleCount > 0;

    /// <summary>Takes a sample.</summary>
    /// <param name="sample">A measured round trip. Negative is refused; zero is allowed, because a
    /// loopback really is that fast and a test should be able to say so.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sample" /> is negative.</exception>
    public void Add(TimeSpan sample) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sample, TimeSpan.Zero);

        var ticks = (double)sample.Ticks;

        if (SampleCount == 0) {
            // The first sample has nothing to average against. RFC 6298 seeds the variance at half
            // the sample rather than at zero, so a link is not assumed to be perfectly steady until
            // it has been watched long enough to know.
            roundTripTicks = ticks;
            varianceTicks = ticks / 2;
        } else {
            varianceTicks = ((1 - VarianceWeight) * varianceTicks) + (VarianceWeight * Math.Abs(roundTripTicks - ticks));
            roundTripTicks = ((1 - SampleWeight) * roundTripTicks) + (SampleWeight * ticks);
        }

        SampleCount++;
    }

    /// <summary>Forgets everything measured, for a reconnect to a different server.</summary>
    public void Reset() {
        roundTripTicks = 0;
        varianceTicks = 0;
        SampleCount = 0;
    }
}
