// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Vixen.Physics.Ecs;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Answers <see cref="CameraOcclusion" />'s one question against the level's collision.</summary>
/// <remarks>
///     <para>
///         <b>The half of Cinemachine's Collider the engine deliberately does not ship.</b>
///         <c>Vixen.Engine</c> references no physics — that is what keeps <c>Vixen.Physics</c> an
///         optional subsystem rather than a dependency of the frame loop — so
///         <see cref="ICameraOcclusion" /> is the seam and this is the four lines on the other side of
///         it. <c>PlayerCameras.ThirdPerson</c> says so in its own remarks: "adding the component is
///         one line once a game has an implementation".
///     </para>
///     <para>
///         <b>A sphere and not a ray.</b> A ray finds the wall and stops the camera exactly on its
///         surface, which puts the near plane through it — the camera would be out of the wall and the
///         image would still have the wall's far side in it. Sweeping the probe radius stops the
///         camera a radius short of everything, which is what the radius is for.
///     </para>
///     <para>
///         ⚠ <b>It sweeps outward from the subject, because the interface says to and because the
///         reason is not obvious.</b> A camera that has already ended up inside the floor is
///         <i>behind</i> that surface, and a sweep back towards the character from there either
///         reports the floor's underside or — with backface culling — nothing at all, which reads as
///         "the way is clear" and leaves the camera where it is. The character is in open space by
///         construction, so a sweep that starts there always finds the first thing between the two.
///     </para>
/// </remarks>
/// <param name="physics">The level's scene, whose <c>World</c> the sweeps run against.</param>
public sealed class ArenaCameraOcclusion(PhysicsScene physics) : ICameraOcclusion {
    /// <summary>The scene being swept.</summary>
    public PhysicsScene Physics { get; } = physics;

    /// <inheritdoc />
    public bool Occluded(Vector3 subject, Vector3 desired, float radius, out Vector3 hit) {
        hit = desired;

        var motion = desired - subject;
        var distance = motion.Length();

        if (distance < 1e-4f) {
            return false;
        }

        // A radius small enough to be a ray is treated as one. Jolt reports a shape that started
        // already overlapping at fraction zero, and a degenerate sphere overlaps whatever the
        // character is standing in the middle of — so the ray is the honest query as well as the
        // cheap one.
        if (radius <= 1e-3f) {
            if (!Physics.World.Raycast(subject, motion / distance, distance, out var ray)) {
                return false;
            }

            hit = ray.Position;
            return true;
        }

        if (!Physics.World.ShapeCast(
                Physics.Shapes.Sphere(radius),
                subject,
                Quaternion.Identity,
                motion,
                out var swept
            )) {
            return false;
        }

        // The sweep's own stopping point rather than the contact point on the surface. `Position` is
        // where the two shapes touched, which is a radius nearer the wall than the camera may be —
        // taking it would put the camera's centre on the surface and undo the sphere.
        hit = subject + (motion * swept.Fraction);
        return true;
    }
}
