// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>
///     Turns the camera so that what it looks at lands where it should in the frame — and leaves it
///     alone while it already does.
/// </summary>
/// <remarks>
///     <para>
///         Cinemachine's Composer, and the stage that does the most for how a game reads. It works
///         in screen space rather than in angles, because framing is a screen-space idea: "keep the
///         boss in the right third of the picture" survives a change of field of view and
///         "keep the camera within 20° of the boss" does not.
///     </para>
///     <para>
///         <b>Three regions, and the middle one is the interesting one.</b> Inside
///         <see cref="DeadZone" /> the camera does not move at all, so a subject fidgeting on the
///         spot does not drag the whole frame about with them. Between the dead zone and
///         <see cref="SoftZone" /> the camera turns towards the target at the rate the damping times
///         allow, which is the lag that makes a camera feel operated rather than bolted on. Outside
///         the soft zone it turns however fast it must: the soft zone is a promise that the subject
///         will not leave it, and a promise a damping time is allowed to break is not one.
///     </para>
///     <para>
///         <b>The horizon stays level.</b> Yaw is applied about the world's up axis and pitch about
///         the camera's own right, so no amount of framing accumulates roll. Roll is
///         <see cref="CameraLens.Dutch" />, it is deliberate, and it is applied somewhere else.
///     </para>
///     <para>
///         ⚠ <b>Perspective only, in the sense that matters.</b> The stage answers a framing error by
///         turning, and where a point lands in an orthographic frame barely depends on which way the
///         camera is turned — so a composer on an orthographic shot will converge slowly or not at
///         all. Framing an orthographic camera is a body's job: <see cref="FramingBody" /> answers
///         the same error by moving, which is what works in that projection.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct ComposerAim {
    /// <summary>An offset added to the target's position before aiming, in world space.</summary>
    /// <remarks>The head rather than the feet — the same job <see cref="FramingBody.TrackedOffset" /> does.</remarks>
    public Vector3 TrackedOffset;

    /// <summary>Where in the frame the target belongs, in normalised device coordinates.</summary>
    /// <remarks><c>(0, 0)</c> is the centre; <c>(1, 1)</c> is the top right corner.</remarks>
    public Vector2 ScreenPosition;

    /// <summary>The half-extent, about <see cref="ScreenPosition" />, in which the camera does not react.</summary>
    public Vector2 DeadZone;

    /// <summary>
    ///     The half-extent, about <see cref="ScreenPosition" />, that the target is not allowed to
    ///     leave. Clamped to at least <see cref="DeadZone" />.
    /// </summary>
    public Vector2 SoftZone;

    /// <summary>How long the camera takes to remove 99 % of a horizontal framing error.</summary>
    public float HorizontalDamping;

    /// <summary>How long the camera takes to remove 99 % of a vertical framing error.</summary>
    public float VerticalDamping;

    /// <summary>
    ///     A composer that holds its subject near the centre with a tenth-screen dead zone and half a
    ///     second of lag.
    /// </summary>
    /// <param name="damping">The damping time on both axes, in seconds.</param>
    /// <returns>The aim.</returns>
    public static ComposerAim Centred(float damping = 0.5f) => new() {
        TrackedOffset = Vector3.Zero,
        ScreenPosition = Vector2.Zero,
        DeadZone = new(0.1f, 0.1f),
        SoftZone = new(0.8f, 0.8f),
        HorizontalDamping = damping,
        VerticalDamping = damping
    };
}

/// <summary>Points the camera straight at the target, every frame, exactly.</summary>
/// <remarks>
///     No dead zone, no damping, no framing. What a debug view, a lock-on and a turret camera want,
///     and the thing to reach for when a composer's lag is being blamed for something else.
/// </remarks>
[Component]
[DataContract]
public struct HardLookAim {
    /// <summary>An offset added to the target's position before aiming, in world space.</summary>
    public Vector3 TrackedOffset;
}

/// <summary>Aims from two angles the game supplies, and looks at nothing.</summary>
/// <remarks>
///     <para>
///         A first-person or free-look camera. Like <see cref="OrbitBody" />, it reads no device:
///         <see cref="Yaw" /> and <see cref="Pitch" /> are written by whatever is steering — a mouse,
///         a stick, a scripted pan — and the aim stage's contribution is the clamp, the level horizon
///         and the damping.
///     </para>
///     <para>
///         <see cref="MinimumPitch" /> and <see cref="MaximumPitch" /> are what stop the player from
///         looking so far up that the world turns over. They are clamped before the rotation is
///         built, not after, so a camera held against the limit has a stable orientation rather than
///         one that flips through the pole.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PovAim {
    /// <summary>Where the camera is turned to, in radians, about the world's up axis.</summary>
    public float Yaw;

    /// <summary>How far up the camera is tilted, in radians. Positive looks up.</summary>
    public float Pitch;

    /// <summary>The furthest down the camera may look, in radians.</summary>
    public float MinimumPitch;

    /// <summary>The furthest up the camera may look, in radians.</summary>
    public float MaximumPitch;

    /// <summary>How long the camera takes to remove 99 % of the difference. Zero is instant.</summary>
    /// <remarks>
    ///     Usually zero. A first-person camera that lags the mouse is a first-person camera that
    ///     feels broken, and the damping is here for the cases that are not being steered by hand.
    /// </remarks>
    public float Damping;

    /// <summary>A level camera that may look 80° up and 80° down, following its input exactly.</summary>
    public static PovAim Default => new() {
        Yaw = 0f,
        Pitch = 0f,
        MinimumPitch = MathUtil.DegreesToRadians(-80f),
        MaximumPitch = MathUtil.DegreesToRadians(80f),
        Damping = 0f
    };
}

/// <summary>Takes the follow target's orientation as the camera's own.</summary>
/// <remarks>
///     Cinemachine's Same As Follow Target. The case for it is the camera bone: an animator has
///     already authored the shot, in the rig, and the last thing the engine should do is have an
///     opinion about it. Also what a cockpit camera wants, where the aircraft's roll <i>is</i> the
///     shot.
/// </remarks>
[Component]
[DataContract]
public struct MatchTargetAim {
    /// <summary>How long the camera takes to remove 99 % of the difference. Zero copies exactly.</summary>
    public float Damping;
}
