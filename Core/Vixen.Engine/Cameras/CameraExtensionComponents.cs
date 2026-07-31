// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>Keeps a shot inside a box, whatever its body stage would rather do.</summary>
/// <remarks>
///     <para>
///         Cinemachine's Confiner, reduced to the case that pays for itself. A box is what a room, a
///         corridor, an arena and a 2D level's bounds all are, it clamps in three components, and it
///         cannot fail — every point outside a box has exactly one nearest point inside it. A convex
///         hull or a polygon needs a containment test, a projection and an answer to what happens
///         when the camera starts outside, and those belong with a level's collision data rather
///         than here.
///     </para>
///     <para>
///         It runs after the body stage and before the aim stage, so a confined camera still looks at
///         its subject correctly from wherever the clamp put it. Confining after the aim would leave
///         the frame pointing at where the camera wanted to be.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct CameraConfiner {
    /// <summary>The low corner of the box the camera must stay in.</summary>
    public Vector3 Minimum;

    /// <summary>The high corner of the box the camera must stay in.</summary>
    public Vector3 Maximum;

    /// <summary>How long the camera takes to remove 99 % of the clamp. Zero clamps outright.</summary>
    /// <remarks>
    ///     Damping the clamp rather than applying it is what makes a camera slide along a wall
    ///     instead of sticking to it. It also means the camera is briefly outside the box, by an
    ///     amount that shrinks exponentially — which is the right trade when the box is a framing
    ///     hint, and the wrong one when it is the edge of the level's geometry. Zero for the latter.
    /// </remarks>
    public float Damping;

    /// <summary>A confiner over a box.</summary>
    /// <param name="bounds">The box.</param>
    /// <param name="damping">The damping time in seconds.</param>
    /// <returns>The confiner.</returns>
    public static CameraConfiner Within(BoundingBox bounds, float damping = 0f) => new() {
        Minimum = bounds.Minimum,
        Maximum = bounds.Maximum,
        Damping = damping
    };

    /// <summary>The box, as a bounding box.</summary>
    public readonly BoundingBox Bounds => new(Minimum, Maximum);
}

/// <summary>Pulls the shot in when something is standing between it and what it is looking at.</summary>
/// <remarks>
///     <para>
///         Cinemachine's Collider. The camera keeps its aim and gives up its distance: when the line
///         from the target to the camera is blocked, the shot moves up that line to just in front of
///         whatever blocked it, and eases back out when the way is clear again.
///     </para>
///     <para>
///         ⚠ <b>It needs something that can answer "is this line blocked", and <c>Vixen.Engine</c>
///         has no such thing.</b> The engine references no physics — that direction is deliberate and
///         is what keeps <c>Vixen.Physics</c> an optional subsystem rather than a dependency of the
///         frame loop. So the question is asked through <see cref="ICameraOcclusion" />, which the
///         host implements over whatever it has: a physics world, a navigation mesh, a hand-written
///         box test in a game whose corridors are all boxes. A shot carrying this component in a game
///         that supplies no implementation is not an error and does nothing.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct CameraOcclusion {
    /// <summary>The radius of the probe, so the camera does not end up flush against a surface.</summary>
    public float Radius;

    /// <summary>How close to the target the camera may be pulled before it stops trying.</summary>
    public float MinimumDistance;

    /// <summary>How long the camera takes to remove 99 % of the pull inward.</summary>
    /// <remarks>
    ///     Usually zero or nearly. A camera that eases into cover spends that time inside the wall,
    ///     which is the one artefact this stage exists to prevent.
    /// </remarks>
    public float PullInDamping;

    /// <summary>How long the camera takes to ease back out once nothing is in the way.</summary>
    /// <remarks>
    ///     Usually much larger than <see cref="PullInDamping" />. The asymmetry is the whole trick:
    ///     going in must be immediate to work at all, and coming out must be slow or a camera brushing
    ///     past a lamp post snaps twice.
    /// </remarks>
    public float PullOutDamping;

    /// <summary>How far from the target the camera is currently allowed to be. Engine-owned.</summary>
    /// <remarks>
    ///     ⚠ <b>The one piece of state in the stage components, and it is here because easing back
    ///     out needs a memory.</b> A body stage recomputes its ideal position from scratch every
    ///     frame, so a correction that is only applied while something is in the way disappears the
    ///     instant it clears — the camera would duck in smoothly and snap out. What has to persist is
    ///     the distance itself, which is what this is. Writing it does nothing but make the camera
    ///     jump once; it is public because a component's fields are, not because it is an input.
    /// </remarks>
    public float Applied;

    /// <summary>An avoider that ducks in at once and comes back out over half a second.</summary>
    /// <param name="radius">The probe radius.</param>
    /// <returns>The extension.</returns>
    public static CameraOcclusion Default(float radius = 0.2f) => new() {
        Radius = radius,
        MinimumDistance = 0.5f,
        PullInDamping = 0f,
        PullOutDamping = 0.5f,
        Applied = 0f
    };
}

/// <summary>What the engine asks when a shot wants to know whether it can see its subject.</summary>
/// <remarks>
///     The seam <see cref="CameraOcclusion" /> is built on, and the only thing the camera system
///     needs to know about a world's solidity. A <c>Vixen.Physics</c>-backed implementation is four
///     lines over a sphere cast; a game with simpler geometry can do better than that.
/// </remarks>
public interface ICameraOcclusion {
    /// <summary>Whether the way from a target to a camera is blocked.</summary>
    /// <param name="subject">The point being looked at — the ray starts here, not at the camera.</param>
    /// <param name="desired">Where the camera would like to be.</param>
    /// <param name="radius">The probe radius. Zero for a ray rather than a sphere.</param>
    /// <param name="hit">Where the first obstruction is, if there is one.</param>
    /// <returns><see langword="true" /> if something is in the way.</returns>
    /// <remarks>
    ///     Cast outward from the subject rather than inward from the camera, and the direction
    ///     matters: a camera that has already ended up inside a wall is <i>behind</i> the surface,
    ///     and an inward cast from there reports the wall's far side or nothing at all. Casting from
    ///     the subject — which is by construction in open space, because the game put a character
    ///     there — always finds the first thing between the two.
    /// </remarks>
    bool Occluded(Vector3 subject, Vector3 desired, float radius, out Vector3 hit);
}
