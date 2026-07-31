// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>The wobble of a camera somebody is holding.</summary>
/// <remarks>
///     <para>
///         Six channels of noise — three of displacement, three of rotation — sampled against the
///         clock rather than integrated frame by frame, so the shake is a function of time and two
///         machines running the same second of game produce the same picture.
///     </para>
///     <para>
///         <b>It is applied after damping and never fed back into it.</b> The shake lives in
///         <see cref="CameraShot.ShakePosition" /> and <see cref="CameraShot.ShakeRotation" />, apart
///         from the position the body stage damped, because a damped camera that could see its own
///         shake would chase it — the noise would become an input to the smoothing that is supposed
///         to be underneath it, and the result reads as a camera with a loose mounting rather than a
///         hand-held one.
///     </para>
///     <para>
///         <b>The amplitudes are bounds, not estimates.</b> <see cref="CameraNoiseSignal" /> is value
///         noise, whose range is exactly ±1, so a shake declared as five centimetres never exceeds
///         five centimetres — which is what makes it safe to put a camera this close to a wall.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct CameraNoise {
    /// <summary>How far the camera may be displaced on each camera-space axis, in world units.</summary>
    public Vector3 PositionAmplitude;

    /// <summary>How quickly each displacement channel varies, in cycles per second.</summary>
    public Vector3 PositionFrequency;

    /// <summary>How far the camera may be turned about each of its own axes, in radians.</summary>
    /// <remarks>X pitches, Y yaws, Z rolls.</remarks>
    public Vector3 RotationAmplitude;

    /// <summary>How quickly each rotation channel varies, in cycles per second.</summary>
    public Vector3 RotationFrequency;

    /// <summary>A multiplier over everything above.</summary>
    /// <remarks>
    ///     The one number a game animates. Winding the gain from 0 to 1 as a player's health drops,
    ///     or to 3 for the duration of an earthquake, keeps the character of the shake and changes
    ///     only its size — which is not what scaling the six amplitudes independently would do.
    /// </remarks>
    public float Gain;

    /// <summary>Which noise this camera gets.</summary>
    /// <remarks>
    ///     Two cameras with the same profile and the same seed shake identically, which is visible
    ///     the moment both are on screen in a split-screen game. It is a field so that they need not.
    /// </remarks>
    public int Seed;

    /// <summary>Whether the shake keeps moving while the game is paused or slowed.</summary>
    /// <remarks>
    ///     False by default, so a bullet-time effect slows the handheld wobble with everything else.
    ///     True is for a camera that must keep breathing behind a pause menu.
    /// </remarks>
    public bool Unscaled;

    /// <summary>A quiet handheld shake: a centimetre or two, half a degree, at a few hertz.</summary>
    public static CameraNoise Handheld => new() {
        PositionAmplitude = new(0.02f, 0.02f, 0.01f),
        PositionFrequency = new(0.6f, 0.9f, 0.4f),
        RotationAmplitude = new(
            MathUtil.DegreesToRadians(0.5f),
            MathUtil.DegreesToRadians(0.5f),
            MathUtil.DegreesToRadians(0.3f)
        ),
        RotationFrequency = new(0.8f, 0.7f, 0.5f),
        Gain = 1f,
        Seed = 0,
        Unscaled = false
    };
}

/// <summary>Something that happened somewhere, hard enough to be felt through the camera.</summary>
/// <remarks>
///     <para>
///         <b>An impulse is an initial velocity, not a displacement.</b> A shell landing gives the
///         camera a shove of so many metres per second in some direction, and what is seen is the
///         ring-down of that shove: a decaying oscillation whose amplitude is
///         <c>|Velocity| / 2πf</c>. That is why doubling <see cref="Frequency" /> halves the visible
///         kick from the same number — a high-frequency rattle and a low-frequency lurch are
///         different events rather than the same one played at different speeds, and expressing them
///         as an amplitude would make them the same one.
///     </para>
///     <para>
///         <b>It has a place, so distance means something.</b> Contributions fall off with
///         <see cref="DissipationDistance" /> and arrive late by
///         <c>distance / <see cref="PropagationSpeed" /></c> — the far explosion is felt smaller and
///         a moment after it is seen, which is free once the impulse knows where it happened.
///     </para>
/// </remarks>
[DataContract]
public struct CameraImpulse {
    /// <summary>Where it happened, in world space.</summary>
    public Vector3 Position;

    /// <summary>The shove, in world units per second. Its direction is the direction of the kick.</summary>
    public Vector3 Velocity;

    /// <summary>How long the ring-down lasts, in seconds. The signal is exactly zero at the end.</summary>
    public float Duration;

    /// <summary>How fast the camera rings, in cycles per second.</summary>
    public float Frequency;

    /// <summary>The distance at which the impulse is no longer felt. Zero means it never fades.</summary>
    public float DissipationDistance;

    /// <summary>
    ///     How fast the impulse travels outward, in world units per second. Zero arrives everywhere
    ///     at once.
    /// </summary>
    public float PropagationSpeed;

    /// <summary>How long ago it happened. Owned by <see cref="CameraImpulses" />.</summary>
    public float Age;

    /// <summary>A short, sharp knock in one direction.</summary>
    /// <param name="position">Where it happened.</param>
    /// <param name="velocity">The shove, in world units per second.</param>
    /// <param name="duration">How long the ring-down lasts.</param>
    /// <param name="dissipation">The distance at which it is no longer felt. Zero never fades.</param>
    /// <returns>The impulse.</returns>
    public static CameraImpulse Bump(
        Vector3 position,
        Vector3 velocity,
        float duration = 0.5f,
        float dissipation = 0f
    ) => new() {
        Position = position,
        Velocity = velocity,
        Duration = duration,
        Frequency = 6f,
        DissipationDistance = dissipation,
        PropagationSpeed = 0f,
        Age = 0f
    };
}

/// <summary>Feels impulses. Put one on a shot that should be shaken by the world.</summary>
/// <remarks>
///     Separate from <see cref="CameraNoise" /> because the two are unrelated: noise is a property of
///     the operator holding the camera and is always there; an impulse is a property of the world and
///     happens to it. A shot can have either, both or neither.
/// </remarks>
[Component]
[DataContract]
public struct CameraImpulseListener {
    /// <summary>How much of the displacement this camera takes. One is all of it.</summary>
    public float PositionGain;

    /// <summary>
    ///     How far the camera swings, in radians per world unit of displacement, about the axis
    ///     across the shove.
    /// </summary>
    /// <remarks>
    ///     A camera shoved upward tilts up, and one shoved sideways yaws — the way a shoulder-mounted
    ///     camera would. Rotation is what makes a shake read at all when the camera is far from
    ///     everything, because a displacement of a few centimetres is invisible at fifty metres and
    ///     a tenth of a degree is not.
    /// </remarks>
    public float RotationGain;

    /// <summary>A listener that takes the whole displacement and a modest swing with it.</summary>
    public static CameraImpulseListener Default => new() { PositionGain = 1f, RotationGain = 0.2f };
}
