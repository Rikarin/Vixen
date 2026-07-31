// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>The noise the six channels are sampled from.</summary>
/// <remarks>
///     <para>
///         <b>Value noise over a one-dimensional lattice</b>, for the reason <c>VfxNoise</c> gives
///         for choosing it over Perlin and simplex — no gradient table to hold, hand around or
///         disagree about — plus one this side cares about more: the range of value noise is exactly
///         the range of its lattice values, so an amplitude is a hard bound. Gradient noise's peak is
///         a number you look up and hope for, and "the shake is 5 cm, except occasionally" is not
///         something a designer framing a shot near geometry can work with.
///     </para>
///     <para>
///         Quintic interpolation, so the first and second derivatives vanish at the lattice points
///         and the motion has no corners in it. A camera is the one place where a discontinuous
///         derivative is plainly visible: the eye reads it as a knock rather than as a wobble.
///     </para>
/// </remarks>
public static class CameraNoiseSignal {
    /// <summary>One channel of noise.</summary>
    /// <param name="time">Where to sample, in lattice units — seconds times a frequency.</param>
    /// <param name="channel">Which of the channels, so that the six do not move together.</param>
    /// <param name="seed">Which noise.</param>
    /// <returns>A value in <c>[−1, 1]</c>.</returns>
    /// <remarks>
    ///     <b>The time is a <see cref="double" /> and the cell is taken from it before anything is
    ///     narrowed.</b> A session's clock runs to tens of thousands of seconds, where a
    ///     <see cref="float" /> has resolved down to hundredths and the wobble has visibly
    ///     quantised — and the usual fix, wrapping the clock, buys the quantisation off with a
    ///     discontinuity every time it wraps. Splitting into an integer cell and a fraction first
    ///     costs one <c>Math.Floor</c> and has neither problem for the next sixty-eight years.
    /// </remarks>
    public static float Sample(double time, int channel, int seed) {
        var cell = (int)Math.Floor(time);
        var blend = Fade((float)(time - cell));

        return MathUtil.Lerp(Lattice(cell, channel, seed), Lattice(cell + 1, channel, seed), blend);
    }

    /// <summary>Three channels at once, one per axis, at three frequencies.</summary>
    /// <param name="seconds">The clock.</param>
    /// <param name="frequency">The cycles per second of each axis.</param>
    /// <param name="firstChannel">The channel index of the X axis; Y and Z follow it.</param>
    /// <param name="seed">Which noise.</param>
    /// <returns>Three values, each in <c>[−1, 1]</c>.</returns>
    public static Vector3 Sample(double seconds, Vector3 frequency, int firstChannel, int seed) => new(
        Sample(seconds * frequency.X, firstChannel, seed),
        Sample(seconds * frequency.Y, firstChannel + 1, seed),
        Sample(seconds * frequency.Z, firstChannel + 2, seed)
    );

    /// <summary>The value at a lattice point.</summary>
    /// <param name="cell">Which point.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="seed">Which noise.</param>
    /// <returns>A value in <c>[−1, 1]</c>.</returns>
    /// <remarks>
    ///     Integer arithmetic all the way to the last line, so the same lattice point produces the
    ///     same bits on every platform the engine builds for — which is what lets a replay of an
    ///     input log reproduce the shake as well as the simulation.
    /// </remarks>
    static float Lattice(int cell, int channel, int seed) {
        var hash = ((uint)cell * 0x9E3779B1u) ^ ((uint)channel * 0x85EBCA77u) ^ ((uint)seed * 0xC2B2AE3Du);

        hash ^= hash >> 15;
        hash *= 0x2C1B3C6Du;
        hash ^= hash >> 12;
        hash *= 0x297A2D39u;
        hash ^= hash >> 15;

        return ((hash & 0xFFFFFFu) * (2f / 0xFFFFFF)) - 1f;
    }

    /// <summary>The quintic smoothstep, whose first two derivatives vanish at both ends.</summary>
    /// <param name="value">A number in <c>[0, 1]</c>.</param>
    /// <returns>The eased number.</returns>
    static float Fade(float value) => value * value * value * ((value * ((value * 6f) - 15f)) + 10f);
}
