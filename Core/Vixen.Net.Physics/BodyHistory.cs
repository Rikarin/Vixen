// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Net.Physics;

/// <summary>Where one body was, and when.</summary>
/// <param name="At">The tick it was captured on.</param>
/// <param name="Position">Where it was.</param>
/// <param name="Rotation">Which way it faced.</param>
public readonly record struct BodyPose(Tick At, Vector3 Position, Quaternion Rotation);

/// <summary>The last few poses of one body, oldest first.</summary>
/// <remarks>
///     <para>
///         The same shape as <c>CaptureRing</c> in the replication layer and for the same reason —
///         a fixed ring, written round, allocating once — but keyed and searched differently. That
///         one is looked up by an exact tick, because a delta names the capture it was measured
///         from. This one is looked up by a tick that <i>falls between</i> two entries, because
///         nobody saw the world on a tick boundary.
///     </para>
///     <para>
///         <b>Ticks are modular, so this cannot be a binary search.</b> A <c>Tick</c> has no
///         ordering — <c>Tick</c>'s own remarks explain why it refuses to have one — and the ring
///         holds at most a couple of dozen entries, so it is walked. Sorting or bisecting a modular
///         sequence is the bug that reproduces once every two years of uptime.
///     </para>
/// </remarks>
public sealed class BodyHistory {
    readonly BodyPose[] entries;
    int count;
    int oldest;

    /// <summary>Creates a history.</summary>
    /// <param name="depth">How many poses to keep.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth" /> is not positive.</exception>
    public BodyHistory(int depth) {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 2);
        entries = new BodyPose[depth];
    }

    /// <summary>How many poses are held.</summary>
    public int Count => count;

    /// <summary>How many it can hold.</summary>
    public int Depth => entries.Length;

    /// <summary>The most recent pose. Undefined when <see cref="Count" /> is zero.</summary>
    public BodyPose Newest => entries[(oldest + count - 1) % entries.Length];

    /// <summary>The furthest back it goes. Undefined when <see cref="Count" /> is zero.</summary>
    public BodyPose Oldest => entries[oldest];

    /// <summary>Records where the body is now.</summary>
    /// <param name="pose">The pose.</param>
    /// <remarks>
    ///     Allocates nothing: this runs once a tick for every tracked body, which is the one place in
    ///     lag compensation that scales with the number of players rather than with the number of
    ///     shots.
    /// </remarks>
    public void Add(in BodyPose pose) {
        if (count == entries.Length) {
            entries[oldest] = pose;
            oldest = (oldest + 1) % entries.Length;

            return;
        }

        entries[(oldest + count) % entries.Length] = pose;
        count++;
    }

    /// <summary>Where the body was at a tick, interpolating between the captures either side of it.</summary>
    /// <param name="at">The tick wanted.</param>
    /// <param name="fraction">
    ///     How far past <paramref name="at" /> to look, from 0 to 1. A client renders between two
    ///     server ticks, so this is the usual case rather than a refinement.
    /// </param>
    /// <param name="interpolate">Whether to interpolate at all, or snap to the nearest capture.</param>
    /// <param name="pose">Where it was.</param>
    /// <returns>Whether the history covers that tick.</returns>
    /// <remarks>
    ///     <para>
    ///         Beyond either end the answer is the end rather than a failure. A target newer than the
    ///         newest capture is a claim about a tick the server has not reached, and the honest
    ///         reading of it is "now"; a target older than the oldest is a claim the window no longer
    ///         covers, and the caller has already clamped it — this is the second line of defence
    ///         rather than the first.
    ///     </para>
    ///     <para>
    ///         The rotation is a <b>normalised lerp rather than a slerp</b>. Between two captures a
    ///         thirtieth of a second apart the two agree to well under the quantisation the rotation
    ///         was replicated at, and slerp costs a trigonometric pair per body per shot. What it
    ///         must do — and does — is take the shorter arc, because a body that rotated past the
    ///         <c>q</c>/<c>-q</c> boundary would otherwise interpolate the long way round and put the
    ///         collider somewhere it never was.
    ///     </para>
    /// </remarks>
    public bool TrySample(Tick at, float fraction, bool interpolate, out BodyPose pose) {
        pose = default;

        if (count == 0) {
            return false;
        }

        if (!interpolate) {
            return TryNearest(at, out pose);
        }

        var newest = Newest;

        // Not a tick this history has reached. Nothing is being hidden — the caller's clamp is what
        // keeps a claim from being in the future, and this is what happens if it did not.
        if (!newest.At.IsAfter(at)) {
            pose = newest;

            return true;
        }

        for (var i = 0; i < count - 1; i++) {
            var left = entries[(oldest + i) % entries.Length];
            var right = entries[(oldest + i + 1) % entries.Length];

            if (left.At.IsAfter(at)) {
                // Past the target already, which means the target is older than everything held.
                break;
            }

            var span = right.At.Subtract(left.At);

            if (span <= 0 || right.At.IsAfter(at) || right.At == at) {
                var offset = span <= 0 ? 0f : (at.Subtract(left.At) + Math.Clamp(fraction, 0f, 1f)) / span;
                pose = Blend(left, right, Math.Clamp(offset, 0f, 1f));

                return true;
            }
        }

        pose = Oldest;

        return true;
    }

    /// <summary>Forgets everything, for a body that has been re-used or a match that has restarted.</summary>
    public void Clear() {
        count = 0;
        oldest = 0;
    }

    static BodyPose Blend(in BodyPose left, in BodyPose right, float amount) {
        if (amount <= 0f) {
            return left;
        }

        if (amount >= 1f) {
            return right;
        }

        // Nlerp rather than Slerp, and the engine's rather than a hand-rolled one — it already takes
        // the shorter arc, which is the part that matters here: q and -q are the same rotation, so a
        // body whose replicated rotation flipped sign between two captures would otherwise
        // interpolate the long way round and put the collider through poses it never held.
        //
        // Slerp is the more correct one and is not worth it. Two captures a thirtieth of a second
        // apart differ by far less than the ten bits the rotation was quantised to on the wire, so
        // the two answers are identical after quantisation and one of them costs a trigonometric
        // pair per body per shot.
        return new(
            left.At,
            Vector3.Lerp(left.Position, right.Position, amount),
            Quaternion.Nlerp(left.Rotation, right.Rotation, amount)
        );
    }

    bool TryNearest(Tick at, out BodyPose pose) {
        pose = entries[oldest];
        var best = long.MaxValue;

        for (var i = 0; i < count; i++) {
            var entry = entries[(oldest + i) % entries.Length];
            var distance = entry.At.Subtract(at);

            // Widened rather than passed to Math.Abs, which throws on int.MinValue — the one value
            // a modular tick distance can take and the one Math.Abs cannot negate. The packet fuzzer
            // found exactly this in TickManager earlier; a tick difference is never an int you may
            // take the absolute value of.
            var magnitude = distance < 0 ? -(long)distance : distance;

            if (magnitude < best) {
                best = magnitude;
                pose = entry;
            }
        }

        return true;
    }
}
