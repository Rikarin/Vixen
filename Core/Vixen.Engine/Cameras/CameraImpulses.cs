// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>
///     The impulses in flight: what emits one writes here, and every listening shot reads from here.
/// </summary>
/// <remarks>
///     <para>
///         The same shape as <c>DebugDraw</c>: an accumulator that anything may write to and one
///         system drains, so a grenade can shake the camera without <c>Vixen.Physics</c> knowing what
///         a camera is. <c>VirtualCameraSystem</c> is what ages it, once a frame, before it samples
///         — so an impulse emitted during <c>Update</c> is felt on the frame it was emitted in.
///     </para>
///     <para>
///         <b>Removal is a swap with the last element</b>, which reorders the list. That is allowed
///         here and nowhere near the coroutine scheduler, because contributions are summed: addition
///         commutes, so the order the impulses are visited in cannot change the answer by more than
///         the last bit of a float.
///     </para>
/// </remarks>
public sealed class CameraImpulses {
    readonly List<CameraImpulse> live = [];

    /// <summary>How many impulses are still ringing.</summary>
    public int Count => live.Count;

    /// <summary>Emits an impulse. It is felt from this frame until its duration is up.</summary>
    /// <param name="impulse">The impulse. Its age is reset.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     If the duration or the frequency is not positive — an impulse that never ends, or that
    ///     does not oscillate, is a mistake rather than a special case, and dividing by either is
    ///     how it would otherwise be discovered.
    /// </exception>
    public void Emit(in CameraImpulse impulse) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(impulse.Duration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(impulse.Frequency);

        var started = impulse;
        started.Age = 0f;
        live.Add(started);
    }

    /// <summary>Ages every impulse and drops the ones that have finished.</summary>
    /// <param name="deltaTime">How much time has passed, in seconds.</param>
    public void Advance(float deltaTime) {
        for (var index = live.Count - 1; index >= 0; index--) {
            var impulse = live[index];
            impulse.Age += deltaTime;

            // The propagation delay is why an impulse outlives its duration: the far listener has
            // not heard it yet. Held until the wave could have reached anything at all.
            var reach = impulse.PropagationSpeed > 0f && impulse.DissipationDistance > 0f
                ? impulse.DissipationDistance / impulse.PropagationSpeed
                : 0f;

            if (impulse.Age >= impulse.Duration + reach) {
                live[index] = live[^1];
                live.RemoveAt(live.Count - 1);
                continue;
            }

            live[index] = impulse;
        }
    }

    /// <summary>Forgets every impulse. For a scene change, where none of them should carry over.</summary>
    public void Clear() => live.Clear();

    /// <summary>The displacement everything in flight adds up to, at a place.</summary>
    /// <param name="listener">Where the camera is.</param>
    /// <returns>The displacement in world space, or zero if nothing is felt there.</returns>
    public Vector3 Sample(Vector3 listener) {
        var total = Vector3.Zero;

        foreach (var impulse in live) {
            var distance = Vector3.Distance(listener, impulse.Position);
            var time = impulse.Age - (impulse.PropagationSpeed > 0f ? distance / impulse.PropagationSpeed : 0f);

            if (time < 0f || time >= impulse.Duration) {
                continue;
            }

            var falloff = 1f;

            if (impulse.DissipationDistance > 0f) {
                falloff = MathUtil.Saturate(1f - (distance / impulse.DissipationDistance));
                falloff *= falloff;
            }

            if (falloff <= 0f) {
                continue;
            }

            // (1 − u)² rather than an exponential, so the signal and its slope are both exactly zero
            // at the end and the impulse can be dropped rather than left ringing below the noise
            // floor for the rest of the level.
            var remaining = 1f - (time / impulse.Duration);
            var envelope = remaining * remaining;
            var angular = MathUtil.TwoPi * impulse.Frequency;

            total += impulse.Velocity * (falloff * envelope * MathF.Sin(angular * time) / angular);
        }

        return total;
    }
}
