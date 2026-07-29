// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Xr;

/// <summary>Which of a stereo pair a view is.</summary>
/// <remarks>
///     A name for what is otherwise an index into an array of two, because "view 0" is not something
///     anybody can check by reading. The index is still what the runtime uses; this is what the game
///     uses.
/// </remarks>
public enum XrEye : byte {
    /// <summary>The left eye — view 0 of a stereo configuration.</summary>
    Left = 0,

    /// <summary>The right eye — view 1.</summary>
    Right = 1
}

/// <summary>Which of the two hands a controller or a hand pose belongs to.</summary>
public enum XrHand : byte {
    /// <summary>The left hand.</summary>
    Left = 0,

    /// <summary>The right hand.</summary>
    Right = 1
}

/// <summary>What poses are measured relative to.</summary>
/// <remarks>
///     <para>
///         Three, which is what OpenXR guarantees and what a game actually distinguishes between.
///         <see cref="Stage" /> is the one a room-scale game wants: its origin is on the floor at the
///         centre of the play area and it does not move, so a chair placed in the world stays where
///         the player left it.
///     </para>
///     <para>
///         <see cref="Local" /> is the seated case — the origin is wherever the headset was when the
///         session started, at eye height — and <see cref="View" /> is head-locked, which is for a
///         reticle and almost nothing else.
///     </para>
/// </remarks>
public enum XrReferenceSpace : byte {
    /// <summary>Fixed relative to the headset's pose when tracking began. Seated experiences.</summary>
    Local = 0,

    /// <summary>Fixed to the floor of the play area. Room-scale experiences.</summary>
    Stage = 1,

    /// <summary>Moves with the headset. For things that must not be looked away from.</summary>
    View = 2
}

/// <summary>Where something is and which way it is facing.</summary>
/// <param name="Position">Where, in the session's reference space, in metres.</param>
/// <param name="Orientation">Which way. Unit length.</param>
/// <remarks>
///     <b>Metres, and not the game's units.</b> A runtime reports the real world and the real world
///     has one scale. A game that works in centimetres scales at the boundary — once, deliberately,
///     in the rig — rather than everywhere, and every VR project that has quietly rescaled poses in
///     six places has ended up with hands that do not line up with the controllers.
/// </remarks>
public readonly record struct XrPose(Vector3 Position, Quaternion Orientation) {
    /// <summary>At the origin, facing the way the space's forward faces.</summary>
    public static XrPose Identity => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>The transform that takes this pose's local space into the reference space.</summary>
    public Matrix4x4 ToMatrix() =>
        Matrix4x4.FromQuaternion(Orientation) * Matrix4x4.FromTranslation(Position);

    /// <summary>The view matrix for a camera at this pose: reference space into eye space.</summary>
    /// <remarks>
    ///     Built from the inverse rotation and the negated position rather than by inverting a
    ///     matrix. A pose's rotation is a unit quaternion, so its inverse is its conjugate and the
    ///     general inverse — a determinant, a division, and the precision that goes with them — is
    ///     arithmetic nobody needs to do twice a frame per eye.
    /// </remarks>
    public Matrix4x4 ToViewMatrix() =>
        Matrix4x4.FromTranslation(-Position) * Matrix4x4.FromQuaternion(Quaternion.Conjugate(Orientation));

    /// <summary>This pose, expressed in the space another pose is the origin of.</summary>
    /// <param name="origin">The other pose — a rig's own transform in the world, typically.</param>
    /// <returns>The composed pose.</returns>
    /// <remarks>
    ///     What turns a headset pose in the reference space into a headset pose in the world: the
    ///     player's rig can be anywhere, and every VR game moves it. Composition rather than addition
    ///     because the rig can be rotated, and a game that only ever added positions works until
    ///     somebody turns the player round with a snap turn.
    /// </remarks>
    public XrPose RelativeTo(in XrPose origin) => new(
        origin.Position + Quaternion.Transform(Position, origin.Orientation),
        origin.Orientation * Orientation
    );
}

/// <summary>The four half-angles of an asymmetric view frustum, in radians.</summary>
/// <param name="AngleLeft">To the left edge. Negative.</param>
/// <param name="AngleRight">To the right edge. Positive.</param>
/// <param name="AngleUp">To the top edge. Positive.</param>
/// <param name="AngleDown">To the bottom edge. Negative.</param>
/// <remarks>
///     <para>
///         <b>Four angles, not a field of view and an aspect ratio.</b> A headset's frustum is not
///         symmetric — the lenses are canted, so each eye sees further towards its own side than
///         towards the nose — and a projection built from a single vertical FOV is wrong by several
///         degrees in a way that shows up as the two eyes disagreeing about where things are.
///     </para>
///     <para>
///         The signs are OpenXR's and they are worth stating: left and down are negative, so the
///         width of the frustum is <c>tan(right) − tan(left)</c> rather than a sum.
///     </para>
/// </remarks>
public readonly record struct XrFieldOfView(
    float AngleLeft,
    float AngleRight,
    float AngleUp,
    float AngleDown
) {
    /// <summary>A symmetric frustum, for a null backend and for tests.</summary>
    /// <param name="horizontal">The full horizontal field of view, in radians.</param>
    /// <param name="vertical">The full vertical field of view, in radians.</param>
    /// <returns>The angles.</returns>
    public static XrFieldOfView Symmetric(float horizontal, float vertical) => new(
        -horizontal * 0.5f,
        horizontal * 0.5f,
        vertical * 0.5f,
        -vertical * 0.5f
    );

    /// <summary>Whether it describes a frustum with any volume in it.</summary>
    public bool IsValid => AngleRight > AngleLeft && AngleUp > AngleDown;
}

/// <summary>One eye's pose and frustum for one frame.</summary>
/// <param name="Pose">Where the eye is, in the session's reference space.</param>
/// <param name="Fov">What it can see.</param>
/// <remarks>
///     Both change every frame and both come from the runtime. The pose in particular is
///     <em>predicted</em> — it is where the eye will be when the frame is displayed, not where it is
///     now — which is why the display time is passed to the call that produces it and why using a
///     pose from the previous frame is visible as judder.
/// </remarks>
public readonly record struct XrView(XrPose Pose, XrFieldOfView Fov) {
    /// <summary>The view matrix for this eye.</summary>
    public Matrix4x4 ViewMatrix => Pose.ToViewMatrix();

    /// <summary>The projection matrix for this eye.</summary>
    /// <param name="nearPlane">The near plane's distance, in metres.</param>
    /// <param name="farPlane">The far plane's distance, or <c>0</c> for an infinite one.</param>
    /// <returns>The projection.</returns>
    public Matrix4x4 Projection(float nearPlane = 0.05f, float farPlane = 0f) =>
        XrProjection.FromFieldOfView(Fov, nearPlane, farPlane);
}

/// <summary>What the runtime says about the headset attached to the machine.</summary>
/// <param name="Name">What to call it in a log — the runtime's own name for the system.</param>
/// <param name="ViewCount">How many views a frame has. Two for stereo, one for a phone-shaped device.</param>
/// <param name="RecommendedImageSize">
///     The per-view render target size the runtime would like. Rendering smaller and letting it
///     upscale is the standard way to buy performance; rendering larger buys sharpness at the centre.
/// </param>
/// <param name="MaximumImageSize">The largest per-view size it will accept.</param>
/// <param name="RecommendedSampleCount">How many samples it would like per pixel.</param>
/// <param name="HasPositionTracking">Whether it tracks where the head is, and not only which way it faces.</param>
public readonly record struct XrSystemInfo(
    string Name,
    int ViewCount,
    Int2 RecommendedImageSize,
    Int2 MaximumImageSize,
    int RecommendedSampleCount,
    bool HasPositionTracking
);
