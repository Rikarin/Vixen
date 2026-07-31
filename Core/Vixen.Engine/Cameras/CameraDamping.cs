// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>
///     How a camera catches up with where it should be: exponentially, and at a rate that does not
///     depend on the frame rate.
/// </summary>
/// <remarks>
///     <para>
///         <b>A damping time is the time in which 99 % of the error is removed</b>, and that number
///         is the whole definition. A residual of <c>0.01</c> after <c>dampTime</c> gives a decay
///         constant of <c>ln(0.01) / dampTime</c>, so the fraction of the remaining error taken in a
///         step of <c>dt</c> is <c>1 − exp(ln(0.01) · dt / dampTime)</c>.
///     </para>
///     <para>
///         <b>Why not a fixed lerp factor.</b> <c>Lerp(current, target, 0.1f)</c> is what this is
///         usually written as, and it means a different camera on a 30 Hz machine than on a 144 Hz
///         one — the same "smoothing" that reads as heavy at 30 fps is nearly a snap at 144. The
///         exponential form composes exactly: the residual after a second is
///         <c>0.01 ^ (1 / dampTime)</c> whether that second arrived as one step or as a hundred,
///         because <c>exp(a·dt)</c> multiplied <c>n</c> times is <c>exp(a·n·dt)</c>. That is not an
///         approximation and <c>DampingIsIndependentOfTheFrameRate</c> holds it to a ten-thousandth.
///     </para>
///     <para>
///         <b>The rotational form is exact too</b>, which is less obvious.
///         <see cref="Quaternion.Slerp" /> travels the geodesic at constant angular speed, so taking
///         a fraction <c>k</c> of the way leaves exactly <c>(1 − k)</c> of the angle — the same
///         multiplicative residual the linear case has. A <see cref="Quaternion.Nlerp" /> would not
///         have that property, which is the reason this is the one place in the engine that pays for
///         a slerp per frame rather than taking the cheaper path.
///     </para>
/// </remarks>
public static class CameraDamping {
    /// <summary>
    ///     The natural logarithm of the residual a damping time is defined to leave.
    /// </summary>
    /// <remarks>
    ///     One percent, matching Cinemachine's convention, so a number copied off a Unity component
    ///     means the same thing here. It is a constant rather than a setting because a per-camera
    ///     residual would make two cameras with the same damping time behave differently, which is
    ///     precisely what the number is for.
    /// </remarks>
    const float NegligibleResidual = -4.605170186f;

    /// <summary>The fraction of the remaining error a step of <paramref name="deltaTime" /> takes.</summary>
    /// <param name="dampTime">The time in which 99 % of the error is removed. Zero snaps.</param>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <returns>A number in <c>[0, 1]</c>: zero moves nothing, one arrives.</returns>
    public static float Fraction(float dampTime, float deltaTime) {
        if (dampTime <= 0f || deltaTime <= 0f) {
            return dampTime <= 0f ? 1f : 0f;
        }

        return 1f - MathF.Exp(NegligibleResidual * deltaTime / dampTime);
    }

    /// <summary>Moves a scalar towards a target.</summary>
    /// <param name="current">Where it is.</param>
    /// <param name="target">Where it should be.</param>
    /// <param name="dampTime">The damping time in seconds. Zero snaps.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <returns>The new value.</returns>
    public static float Approach(float current, float target, float dampTime, float deltaTime) =>
        current + ((target - current) * Fraction(dampTime, deltaTime));

    /// <summary>Moves a vector towards a target, one damping time per axis.</summary>
    /// <param name="current">Where it is.</param>
    /// <param name="target">Where it should be.</param>
    /// <param name="dampTime">The damping time of each axis, in seconds.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <returns>The new value.</returns>
    /// <remarks>
    ///     Per axis, because the axes are not alike: a third-person camera that follows its subject
    ///     loosely from behind and tightly in height wants a second of damping on Z and a tenth on
    ///     Y. Which space those axes are in is the caller's business — every body stage that uses
    ///     this damps in the space its offset was authored in, so the numbers mean what the person
    ///     typing them thought they meant.
    /// </remarks>
    public static Vector3 Approach(Vector3 current, Vector3 target, Vector3 dampTime, float deltaTime) =>
        new(
            Approach(current.X, target.X, dampTime.X, deltaTime),
            Approach(current.Y, target.Y, dampTime.Y, deltaTime),
            Approach(current.Z, target.Z, dampTime.Z, deltaTime)
        );

    /// <summary>Turns a rotation towards a target.</summary>
    /// <param name="current">Where it points.</param>
    /// <param name="target">Where it should point.</param>
    /// <param name="dampTime">The damping time in seconds. Zero snaps.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <returns>The new rotation.</returns>
    public static Quaternion Approach(
        Quaternion current,
        Quaternion target,
        float dampTime,
        float deltaTime
    ) =>
        Quaternion.Slerp(current, target, Fraction(dampTime, deltaTime));

    /// <summary>Shrinks an error towards zero — the same curve, written the way a correction wants it.</summary>
    /// <param name="error">How far off it is.</param>
    /// <param name="dampTime">The damping time in seconds. Zero removes the error at once.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <returns>What is left of the error after the step.</returns>
    /// <remarks>
    ///     Every body stage computes an ideal position and then damps the offset <em>from</em> it, so
    ///     this is the form the arithmetic actually takes. Writing it as
    ///     <c>Approach(current, ideal, …)</c> would be the same number and would hide that the thing
    ///     being damped is a residual, which is what makes the composition exact.
    /// </remarks>
    public static Vector3 Decay(Vector3 error, Vector3 dampTime, float deltaTime) =>
        Approach(error, Vector3.Zero, dampTime, deltaTime);
}
