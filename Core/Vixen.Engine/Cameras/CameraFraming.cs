// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>Where a point lands in the frame, and what a frame is at a given depth.</summary>
/// <remarks>
///     <para>
///         The arithmetic <see cref="FramingBody" /> and <see cref="ComposerAim" /> share. Both work
///         in normalised device coordinates — <c>(0, 0)</c> the centre, <c>±1</c> the edges — because
///         that is the only space in which "keep them in the left third" is one number rather than a
///         number and a field of view and a distance.
///     </para>
///     <para>
///         A restatement of the projection <c>CameraMath</c> already builds, specialised to a single
///         point and to the stages that need it without a matrix.
///     </para>
///     <para>
///         ⚠ <b>This was internal, and the reason it stopped being internal is the reason it was
///         internal.</b> The note here read "a second public way to project a point is a second place
///         for the reverse-Z convention to be got wrong" — and then a second consumer arrived, in
///         another assembly: <c>Vixen.Animation.Constraints</c>'s <c>ScreenFrame</c>, which asks where
///         a camera would have to be for a subject to land at a given place in the frame. Keeping this
///         internal would have meant a copy of the convention over there, which is exactly what the
///         note was against. One implementation, shared, is what it was asking for.
///     </para>
/// </remarks>
public static class CameraFraming {
    /// <summary>
    ///     Half the frame's width and height: at unit depth for a perspective lens, in world units
    ///     for an orthographic one.
    /// </summary>
    /// <param name="lens">The lens.</param>
    /// <param name="aspectRatio">Width over height.</param>
    /// <returns>The half-extents.</returns>
    public static Vector2 Extents(in CameraLens lens, float aspectRatio) {
        if (lens.Orthographic) {
            var height = lens.OrthographicHeight * 0.5f;
            return new(height * aspectRatio, height);
        }

        var vertical = MathF.Tan(lens.FieldOfView * 0.5f);
        return new(vertical * aspectRatio, vertical);
    }

    /// <summary>Where a world-space point lands in the frame.</summary>
    /// <param name="point">The point.</param>
    /// <param name="position">Where the camera is.</param>
    /// <param name="rotation">Which way it looks.</param>
    /// <param name="lens">Its lens.</param>
    /// <param name="aspectRatio">Width over height.</param>
    /// <param name="screen">Where it lands, in normalised device coordinates.</param>
    /// <param name="depth">How far in front of the camera it is.</param>
    /// <returns><see langword="false" /> if the point is behind the camera, where there is no answer.</returns>
    public static bool Project(
        Vector3 point,
        Vector3 position,
        Quaternion rotation,
        in CameraLens lens,
        float aspectRatio,
        out Vector2 screen,
        out float depth
    ) {
        var view = ToViewSpace(point - position, rotation);
        var extents = Extents(in lens, aspectRatio);

        // The camera faces its local −Z, so what is in front of it has a negative Z in view space.
        depth = -view.Z;

        if (lens.Orthographic) {
            screen = new(view.X / extents.X, view.Y / extents.Y);
            return depth > 0f;
        }

        if (depth <= MathUtil.ZeroTolerance) {
            screen = Vector2.Zero;
            return false;
        }

        screen = new(view.X / (depth * extents.X), view.Y / (depth * extents.Y));
        return true;
    }

    /// <summary>Takes a world-space direction into the camera's own axes.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="rotation">The camera's rotation.</param>
    /// <returns>The direction in view space.</returns>
    public static Vector3 ToViewSpace(Vector3 direction, Quaternion rotation) =>
        Quaternion.Transform(direction, Quaternion.Conjugate(rotation));

    /// <summary>
    ///     How far past a dead zone a coordinate is: zero inside it, and the signed overshoot
    ///     outside.
    /// </summary>
    /// <param name="value">Where the target is.</param>
    /// <param name="centre">Where it should be.</param>
    /// <param name="halfExtent">How far it may stray before anything happens.</param>
    /// <returns>The error the stage has to remove.</returns>
    public static float Overshoot(float value, float centre, float halfExtent) {
        var error = value - centre;
        var slack = MathF.Abs(halfExtent);

        if (MathF.Abs(error) <= slack) {
            return 0f;
        }

        return error - (MathF.Sign(error) * slack);
    }

    /// <summary>
    ///     The angle a camera would have to turn through to bring a point from where it is on screen
    ///     to the nearest edge of the region it belongs in.
    /// </summary>
    /// <param name="screen">Where the point is, in normalised device coordinates.</param>
    /// <param name="centre">Where it belongs.</param>
    /// <param name="halfExtent">How far it may stray before anything happens.</param>
    /// <param name="tangent">The half-frame at unit depth on this axis.</param>
    /// <param name="edge">Where the correction would put it, in normalised device coordinates.</param>
    /// <returns>The angle, in radians, and zero while the point is already where it belongs.</returns>
    /// <remarks>
    ///     ⚠ <b>The difference of two arctangents, not the arctangent of a difference.</b> Screen
    ///     coordinates are proportional to the <i>tangent</i> of the angle off the view axis, so a
    ///     correction computed as <c>atan(overshoot · tan(fov/2))</c> is right near the middle of the
    ///     frame and overshoots increasingly towards its edges — a subject entering from the side is
    ///     pulled past the dead zone and towards the centre, which reads as the camera snatching at
    ///     it. The exact form costs one more <c>atan</c>.
    /// </remarks>
    public static float TurnToEdge(float screen, float centre, float halfExtent, float tangent, out float edge) {
        var slack = MathF.Abs(halfExtent);
        edge = Math.Clamp(screen, centre - slack, centre + slack);

        return MathF.Atan(screen * tangent) - MathF.Atan(edge * tangent);
    }

    /// <summary>
    ///     How much of an overshoot to take this frame: the damped share, or more if the damped share
    ///     would leave the target outside the region it is not allowed to leave.
    /// </summary>
    /// <param name="overshoot">How far past the dead zone the target is.</param>
    /// <param name="slack">How far past the dead zone it is allowed to stay — the soft zone's margin.</param>
    /// <param name="dampTime">The damping time, in seconds.</param>
    /// <param name="deltaTime">The step, in seconds.</param>
    /// <returns>The correction to apply, in the same units.</returns>
    /// <remarks>
    ///     This is the part that makes a soft zone a guarantee rather than a suggestion. Damping
    ///     alone can be outrun by anything that moves faster than it converges, and a camera that
    ///     falls behind a sprinting subject and then never catches up has lost the subject; taking
    ///     the larger of the damped correction and the one that just barely holds the line means the
    ///     lag is real right up until the moment it would cost the shot.
    /// </remarks>
    public static float Correction(float overshoot, float slack, float dampTime, float deltaTime) {
        var damped = overshoot * CameraDamping.Fraction(dampTime, deltaTime);
        var required = MathF.Abs(overshoot) - MathF.Abs(slack);

        if (required <= 0f) {
            return damped;
        }

        var floor = MathF.Sign(overshoot) * required;
        return MathF.Abs(damped) >= MathF.Abs(floor) ? damped : floor;
    }
}
