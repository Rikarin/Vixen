// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>Which frame of reference a follow offset is expressed in.</summary>
/// <remarks>
///     The choice decides two things at once — where the camera ends up, and which axes the damping
///     times apply to — and they are the same choice on purpose. "Two seconds of damping behind the
///     car and none at all sideways" is only a sentence that means something if the offset and the
///     damping agree about which way "behind" is.
/// </remarks>
public enum CameraBinding {
    /// <summary>
    ///     World axes. The camera keeps a fixed offset in world space, so the target may turn
    ///     underneath it without the view swinging round — an isometric or a side-on game.
    /// </summary>
    World,

    /// <summary>
    ///     The target's own axes, roll included. The camera is welded to the target's frame, which is
    ///     what a cockpit or a chase camera in a barrel roll wants and what everything else does not.
    /// </summary>
    TargetRotation,

    /// <summary>
    ///     The target's heading — its facing flattened onto the horizontal plane. The camera swings
    ///     round to stay behind a turning subject without ever leaving the level, which is the
    ///     third-person default.
    /// </summary>
    TargetHeading,

    /// <summary>
    ///     The direction the camera already lies in. Only the distance and height of the offset are
    ///     kept, so the camera trails the target without ever being pushed round by it — the "lazy"
    ///     follow that never fights a player who is orbiting with the stick.
    /// </summary>
    SimpleFollow
}

/// <summary>Keeps the camera at a fixed offset from what it follows.</summary>
/// <remarks>
///     The workhorse, and Cinemachine's Transposer. Everything it does is in
///     <see cref="Binding" />'s frame: the offset is measured in it, and each damping time in
///     <see cref="Damping" /> applies to one of its axes.
/// </remarks>
[Component]
[DataContract]
public struct FollowBody {
    /// <summary>Where the camera sits relative to the target, in <see cref="Binding" />'s axes.</summary>
    public Vector3 Offset;

    /// <summary>Which frame <see cref="Offset" /> and <see cref="Damping" /> are in.</summary>
    public CameraBinding Binding;

    /// <summary>
    ///     How long the camera takes to remove 99 % of its error on each of the binding's axes.
    /// </summary>
    public Vector3 Damping;

    /// <summary>A camera a set distance behind and above the target, swinging round as it turns.</summary>
    /// <param name="distance">How far behind.</param>
    /// <param name="height">How far above.</param>
    /// <param name="damping">The damping time on every axis, in seconds.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    ///     Behind is +Z, because an entity faces its local −Z (see <c>Conventions.md</c>
    ///     § Handedness).
    /// </remarks>
    public static FollowBody Behind(float distance, float height, float damping = 0.5f) => new() {
        Offset = new(0f, height, distance),
        Binding = CameraBinding.TargetHeading,
        Damping = new(damping, damping, damping)
    };
}

/// <summary>
///     Keeps the target at a chosen place on the screen, at a chosen distance, and does nothing at
///     all while it is already close enough to it.
/// </summary>
/// <remarks>
///     <para>
///         Cinemachine's Framing Transposer, and the body a third-person or 2D-follow camera
///         usually wants. It differs from <see cref="FollowBody" /> in the thing it is trying to
///         hold constant: not a position in the world, but a position in the <i>frame</i>. The
///         camera moves only enough to bring the target back inside <see cref="DeadZone" />, which
///         is what stops a follow camera from twitching at every small movement of a character who
///         is essentially standing still.
///     </para>
///     <para>
///         ⚠ <b>It frames using the rotation the shot had at the end of the previous frame</b>,
///         because the aim stage that decides this frame's rotation has not run yet — the body
///         stage is what feeds it a position. Cinemachine has the same one-frame relationship and
///         for the same reason. It is invisible while the camera is turning at any sane rate, and
///         the alternative is a fixed point iteration between the two stages every frame.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct FramingBody {
    /// <summary>An offset added to the target's position before framing, in world space.</summary>
    /// <remarks>Where to aim on a character: the head or the chest, rather than the feet.</remarks>
    public Vector3 TrackedOffset;

    /// <summary>How far from the target the camera tries to sit.</summary>
    public float Distance;

    /// <summary>Where in the frame the target belongs, in normalised device coordinates.</summary>
    /// <remarks>
    ///     <c>(0, 0)</c> is the centre of the screen, <c>(−1, −1)</c> the bottom left and
    ///     <c>(1, 1)</c> the top right. A driving game puts it a little below centre; a shoulder
    ///     camera puts it off to one side.
    /// </remarks>
    public Vector2 ScreenPosition;

    /// <summary>
    ///     How far from <see cref="ScreenPosition" /> the target may drift before the camera reacts,
    ///     as a half-extent in the same normalised coordinates.
    /// </summary>
    /// <remarks>
    ///     Zero is a rigid frame — every movement is answered. <c>(1, 1)</c> never reacts at all,
    ///     since nothing on screen is further from the centre than that.
    /// </remarks>
    public Vector2 DeadZone;

    /// <summary>The closest the camera may come to the target. Zero for no limit.</summary>
    public float MinimumDistance;

    /// <summary>The furthest the camera may be from the target. Zero for no limit.</summary>
    public float MaximumDistance;

    /// <summary>
    ///     Damping in camera space: X across the frame, Y up it, Z along the view axis.
    /// </summary>
    public Vector3 Damping;

    /// <summary>A camera framing its target dead centre at a distance, with a tenth-screen dead zone.</summary>
    /// <param name="distance">How far away.</param>
    /// <param name="damping">The damping time on every axis, in seconds.</param>
    /// <returns>The body.</returns>
    public static FramingBody At(float distance, float damping = 0.5f) => new() {
        TrackedOffset = Vector3.Zero,
        Distance = distance,
        ScreenPosition = Vector2.Zero,
        DeadZone = new(0.1f, 0.1f),
        MinimumDistance = 0f,
        MaximumDistance = 0f,
        Damping = new(damping, damping, damping)
    };
}

/// <summary>Puts the camera on a sphere about the target, at an angle the game chooses.</summary>
/// <remarks>
///     <para>
///         Cinemachine's Orbital Transposer, with one deliberate difference: <b>it reads no
///         device.</b> <see cref="Heading" /> and <see cref="Pitch" /> are two numbers that gameplay
///         writes — from a stick, from a mouse, from a cutscene's animation curve, from a tween that
///         recentres the camera behind the player after a few idle seconds — and the body's job is
///         only to turn them into a position and damp the result. Binding a camera component
///         directly to an input action would make every one of those cases the exception.
///     </para>
///     <para>
///         Damping is in the orbit's own frame, so <see cref="Damping" />'s X is how loosely the
///         camera swings round, Y is how loosely it rises, and Z is how loosely it pushes in and out.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct OrbitBody {
    /// <summary>An offset added to the target's position to give the point orbited, in world space.</summary>
    public Vector3 PivotOffset;

    /// <summary>How far from the pivot the camera orbits.</summary>
    public float Radius;

    /// <summary>
    ///     The angle round the pivot, in radians. Zero puts the camera behind the target — at +Z,
    ///     since an entity faces its local −Z — and a quarter turn puts it off the target's right.
    /// </summary>
    public float Heading;

    /// <summary>
    ///     How far above the horizontal the camera rides, in radians. Positive looks down on the
    ///     target.
    /// </summary>
    public float Pitch;

    /// <summary>Damping in the orbit's frame: X tangential, Y vertical, Z radial.</summary>
    public Vector3 Damping;

    /// <summary>An orbit at a radius, level with its pivot.</summary>
    /// <param name="radius">How far out.</param>
    /// <param name="pitch">How far above the horizontal, in radians.</param>
    /// <param name="damping">The damping time on every axis, in seconds.</param>
    /// <returns>The body.</returns>
    public static OrbitBody At(float radius, float pitch = 0f, float damping = 0.3f) => new() {
        PivotOffset = Vector3.Zero,
        Radius = radius,
        Heading = 0f,
        Pitch = pitch,
        Damping = new(damping, damping, damping)
    };
}

/// <summary>Puts the camera exactly on the target, with no damping and no argument.</summary>
/// <remarks>
///     A first-person camera, a camera bone driven by an animation, a camera on a rail that
///     something else moves. There is nothing to damp because there is no error: the camera is where
///     the target is, this frame, exactly.
/// </remarks>
[Component]
[DataContract]
public struct HardLockBody {
    /// <summary>An offset from the target.</summary>
    public Vector3 Offset;

    /// <summary>Whether <see cref="Offset" /> is in the target's axes rather than the world's.</summary>
    /// <remarks>
    ///     True for the eye position on a character, which has to turn with the head; false for a
    ///     fixed height above a vehicle that rolls.
    /// </remarks>
    public bool InTargetSpace;
}
