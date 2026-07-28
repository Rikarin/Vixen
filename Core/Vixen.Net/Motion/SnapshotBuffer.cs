// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Net.Motion;

/// <summary>Where something was, at a tick.</summary>
/// <param name="Tick">The tick the server stamped on it.</param>
/// <param name="Position">Where it was.</param>
/// <param name="Rotation">Which way it faced.</param>
public readonly record struct TransformSample(Tick Tick, Vector3 Position, Quaternion Rotation);

/// <summary>How a buffer behaves when it runs out of what it needs.</summary>
public sealed record SnapshotBufferOptions {
    /// <summary>
    ///     How far past the newest sample motion may be guessed at before it is held instead.
    /// </summary>
    /// <remarks>
    ///     Clamped rather than unbounded, because extrapolation is a guess that gets worse the longer
    ///     it runs: a player who stopped a second ago would otherwise still be walking through the
    ///     wall on everybody else's screen. Four ticks is a fifth of a second at the default rate —
    ///     long enough to ride out a lost packet, short enough that a lost connection stops.
    /// </remarks>
    public int MaxExtrapolationTicks { get; init; } = 4;

    /// <summary>
    ///     How far apart two consecutive samples have to be before the object is taken to have
    ///     teleported rather than moved.
    /// </summary>
    /// <remarks>
    ///     Without this, a respawn on the other side of the map is a very fast walk through
    ///     everything in between. With it, the object is where it is on the next frame — which is
    ///     also what actually happened.
    /// </remarks>
    public float SnapDistance { get; init; } = 5f;
}

/// <summary>
///     The last few places something was, and where it therefore is now.
/// </summary>
/// <remarks>
///     <para>
///         A client draws the world <b>behind</b> the server, not at it: at
///         <c>TickManager.InterpolationTick</c>, far enough back that the snapshots bracketing the
///         moment being drawn have already arrived. That is what makes motion smooth on a connection
///         that delivers in bursts — the buffer is the delay, and the delay is what buys the
///         interpolation something to interpolate between.
///     </para>
///     <para>
///         Rotation is held rather than extrapolated. A position that overshoots comes back with the
///         next snapshot and reads as momentum; a rotation that overshoots reads as a stumble, and
///         there is no rotational equivalent of the position's constant velocity that is worth
///         assuming.
///     </para>
/// </remarks>
public sealed class SnapshotBuffer {
    readonly TransformSample[] samples;

    int count;
    int oldest;

    /// <summary>How it behaves when it runs out.</summary>
    public SnapshotBufferOptions Options { get; }

    /// <summary>How many samples are held.</summary>
    public int Count => count;

    /// <summary>How many it can hold before the oldest goes.</summary>
    public int Capacity => samples.Length;

    /// <summary>Samples ignored because something newer had already arrived.</summary>
    public long StaleCount { get; private set; }

    /// <summary>Times a value was interpolated between two samples, which is the ordinary case.</summary>
    public long InterpolatedCount { get; private set; }

    /// <summary>Times motion past the newest sample had to be guessed at.</summary>
    public long ExtrapolatedCount { get; private set; }

    /// <summary>Times two samples were too far apart to move between, so the object jumped.</summary>
    public long SnappedCount { get; private set; }

    /// <summary>Times there was nothing to work with and the newest or oldest was held.</summary>
    public long StarvedCount { get; private set; }

    /// <summary>Creates a buffer.</summary>
    /// <param name="capacity">
    ///     How many samples to keep. The default is a second at the default tick rate, which is more
    ///     than interpolation needs and about what a diagnostic overlay wants to draw.
    /// </param>
    /// <param name="options">How it behaves when it runs out.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is below two.</exception>
    public SnapshotBuffer(int capacity = 32, SnapshotBufferOptions? options = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

        samples = new TransformSample[capacity];
        Options = options ?? new SnapshotBufferOptions();
    }

    /// <summary>The newest sample held.</summary>
    /// <exception cref="InvalidOperationException">There are none.</exception>
    public TransformSample Newest => count == 0 ? throw Empty() : samples[(oldest + count - 1) % samples.Length];

    /// <summary>The oldest sample held.</summary>
    /// <exception cref="InvalidOperationException">There are none.</exception>
    public TransformSample Oldest => count == 0 ? throw Empty() : samples[oldest];

    /// <summary>Takes a sample.</summary>
    /// <param name="sample">Where the thing was.</param>
    /// <returns>
    ///     Whether it was kept. A sample no newer than one already held is dropped: transforms travel
    ///     unreliably and out of order, and an old one has nothing to add to a newer one.
    /// </returns>
    public bool Add(in TransformSample sample) {
        if (count != 0 && !sample.Tick.IsAfter(Newest.Tick)) {
            StaleCount++;

            return false;
        }

        if (count == samples.Length) {
            oldest = (oldest + 1) % samples.Length;
            count--;
        }

        samples[(oldest + count) % samples.Length] = sample;
        count++;

        return true;
    }

    /// <summary>Forgets everything, for an object that has just been spawned again.</summary>
    public void Clear() {
        count = 0;
        oldest = 0;
    }

    /// <summary>Works out where the thing is at a moment between ticks.</summary>
    /// <param name="tick">The tick being drawn — the interpolation tick, not the simulation's.</param>
    /// <param name="fraction">How far through that tick, from 0 to 1.</param>
    /// <param name="sample">Where it is.</param>
    /// <returns>Whether there was anything to say. False only when nothing has arrived at all.</returns>
    public bool TrySample(Tick tick, float fraction, out TransformSample sample) {
        sample = default;

        if (count == 0) {
            StarvedCount++;

            return false;
        }

        var target = Math.Clamp(fraction, 0f, 1f);

        if (count == 1) {
            StarvedCount++;
            sample = At(0) with { Tick = tick };

            return true;
        }

        // Before anything we hold: the object has not started moving as far as this client knows.
        if (tick.IsBefore(At(0).Tick)) {
            StarvedCount++;
            sample = At(0) with { Tick = tick };

            return true;
        }

        for (var i = 0; i < count - 1; i++) {
            var from = At(i);
            var to = At(i + 1);
            var span = to.Tick.Subtract(from.Tick);
            var offset = tick.Subtract(from.Tick) + target;

            if (offset < 0 || offset > span) {
                continue;
            }

            if (Vector3.DistanceSquared(from.Position, to.Position) > Options.SnapDistance * Options.SnapDistance) {
                // Too far to have walked. Whatever happened, it happened — so be where it ended up
                // rather than sliding through everything in between.
                SnappedCount++;
                sample = to with { Tick = tick };

                return true;
            }

            var amount = span == 0 ? 0f : offset / span;
            InterpolatedCount++;

            sample = new(
                tick,
                Vector3.Lerp(from.Position, to.Position, amount),
                Quaternion.Slerp(from.Rotation, to.Rotation, amount)
            );

            return true;
        }

        return Extrapolate(tick, target, out sample);
    }

    bool Extrapolate(Tick tick, float fraction, out TransformSample sample) {
        var newest = At(count - 1);
        var previous = At(count - 2);
        var ahead = tick.Subtract(newest.Tick) + fraction;

        if (ahead <= 0) {
            StarvedCount++;
            sample = newest with { Tick = tick };

            return true;
        }

        ExtrapolatedCount++;

        if (ahead > Options.MaxExtrapolationTicks) {
            // Past the clamp the guess stops rather than running away with itself.
            StarvedCount++;
            ahead = Options.MaxExtrapolationTicks;
        }

        var span = newest.Tick.Subtract(previous.Tick);
        var velocity = span == 0 ? Vector3.Zero : (newest.Position - previous.Position) / span;

        sample = new(tick, newest.Position + (velocity * ahead), newest.Rotation);

        return true;
    }

    TransformSample At(int index) => samples[(oldest + index) % samples.Length];

    static InvalidOperationException Empty() => new("The buffer has no samples.");
}
