// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Net.Motion;

/// <summary>
///     Hides the server correcting the local player, without lying about where they are.
/// </summary>
/// <remarks>
///     <para>
///         The owner of an object does not interpolate it — they simulate it, from their own input,
///         on the frame they pressed the key. That is the whole reason a local player feels
///         responsive and everyone else looks smooth, and it is why owner-side motion is a different
///         problem from <see cref="SnapshotBuffer" />'s.
///     </para>
///     <para>
///         But the server is the authority, so sometimes it says the player is somewhere else. Moving
///         them there is correct and looks like a twitch. So the <b>simulation</b> takes the
///         correction immediately — the next physics step, the next hit test and everything the
///         server will judge all run from the right place — and the <b>camera</b> is given the error
///         as an offset that decays away over a few frames. What the player sees glides; what the
///         game computes is already right.
///     </para>
///     <para>
///         Past <see cref="SnapDistance" /> there is no gliding: a correction that large is a respawn
///         or a rubber-band, and dragging the camera across the map is worse than putting it there.
///     </para>
/// </remarks>
public sealed class OwnerSmoothing {
    /// <summary>How long the visible half of an error takes to disappear.</summary>
    /// <remarks>
    ///     A half-life rather than a speed, so the correction is frame-rate independent and finishes
    ///     in the same wall-clock time at 30 frames a second as at 240.
    /// </remarks>
    public TimeSpan HalfLife { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>How large a correction stops being smoothed and is simply shown.</summary>
    public float SnapDistance { get; init; } = 3f;

    /// <summary>What is still being worked off.</summary>
    public Vector3 Error { get; private set; }

    /// <summary>Whether anything is being worked off right now.</summary>
    public bool IsSmoothing => Error.LengthSquared() > 0f;

    /// <summary>Corrections taken.</summary>
    public long CorrectionCount { get; private set; }

    /// <summary>Corrections too large to smooth, which were shown instead.</summary>
    public long SnapCount { get; private set; }

    /// <summary>
    ///     Takes a correction: the simulation is about to move from one place to another, and this is
    ///     what the eye should not see happen all at once.
    /// </summary>
    /// <param name="from">Where the client had itself.</param>
    /// <param name="to">Where the server says it is.</param>
    public void Correct(in Vector3 from, in Vector3 to) {
        CorrectionCount++;
        var error = from - to;

        if (error.LengthSquared() > SnapDistance * SnapDistance) {
            SnapCount++;
            Error = Vector3.Zero;

            return;
        }

        // Added rather than replaced: a second correction while the first is still being worked off
        // is a second error on top of a remaining one, and dropping the remainder would put the
        // visible position back where the first correction already decided it should not be.
        Error += error;
    }

    /// <summary>Where to draw the owner this frame.</summary>
    /// <param name="simulated">Where the simulation has them, which is where they actually are.</param>
    /// <param name="elapsed">The frame's time.</param>
    /// <returns>Where to put the visual.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed" /> is negative.</exception>
    public Vector3 Apply(in Vector3 simulated, TimeSpan elapsed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (!IsSmoothing) {
            return simulated;
        }

        var half = HalfLife.TotalSeconds;
        var decay = half <= 0 ? 0f : (float)Math.Pow(0.5, elapsed.TotalSeconds / half);
        Error *= decay;

        // Below a millimetre there is nothing left to look at, and letting it run makes IsSmoothing
        // true for ever on a link that never quite agrees.
        if (Error.LengthSquared() < 1e-6f) {
            Error = Vector3.Zero;
        }

        return simulated + Error;
    }

    /// <summary>Drops what is being worked off, for an object that has just been put somewhere.</summary>
    public void Reset() => Error = Vector3.Zero;
}
